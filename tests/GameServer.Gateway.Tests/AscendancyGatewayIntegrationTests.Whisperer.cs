using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    internal static readonly ushort[][] WhispererBuilds =
    [
        [17123, 4174, 1116, 22720, 27553, 50932, 15390, 26415],
        [30419, 53715, 21095, 2266, 540, 863, 9982, 5888],
        [17123, 4174, 1116, 22720, 24726, 23799, 9982, 5888],
    ];
    static uint WKey(string name) => Program.Stats.Key(name)!.Value;
    static int WStat(ClientState conn, string name) => Program.Stats.Key(name) is uint key
        ? Program.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
    static GatewayCharacter WhispererGame(ushort[]? nodes = null, int refund = 100) =>
        new(classRow: 3, baseClass: 2, choice: "Whisperer", nodes: nodes, refundPoints: (ushort)refund, level: 68);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Whisperer_builds_allocate_all_real_nodes_pay_40_to_refund_and_reload(int build)
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame();
        Assert.Null((await game.Change(WhispererBuilds[build], true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        Assert.Equal(24, WStat(game.State, "maximum_mana_+%"));
        Assert.Equal(40, WStat(game.State, "attack_damage_+%"));
        Assert.Equal(WhispererBuilds[build].Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Null((await game.Change(WhispererBuilds[build].Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Equal((ushort)60, game.State.RefundPoints); Assert.Empty(game.Reload().MainAscendancy.Allocated);
        Assert.Null((await game.Change(WhispererBuilds[build], true)).Error);
    }
    [Fact]
    public async Task Infinite_pursuit_replaces_only_Intelligence_mana_and_uses_final_Dexterity_once()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame([17123, 4174]);
        var c = Program.Skills.ClassFor(3);
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 32) * 1.06m), game.State.Pools.Mana);
        await game.Command("/set_stat base_dexterity 8");
        await game.Command("/set_stat dexterity_+% 50");
        Assert.Equal(60, game.State.Pools.Dexterity);
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 60) * 1.06m), game.State.Pools.Mana);
        await game.Command("/set_stat base_intelligence 100");
        Assert.Equal(114, game.State.Pools.Intelligence);
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 60) * 1.06m), game.State.Pools.Mana);
        Assert.Null((await game.Change([4174], false, refundPoints: true)).Error);
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 57) * 1.06m), game.State.Pools.Mana);
    }
    [Fact]
    public void Physical_mana_mitigation_follows_ES_preserves_other_types_and_recoups_the_hit_once()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State; conn.PlayerMana = 60; conn.EnergyShield.SetMaximum(30, true);
        var stats = Program.CharacterPlayerStats(conn); double now = conn.Clock.Elapsed.TotalSeconds;
        Assert.Equal(30, Program.WhispererDamagePlayerHit(conn, 100, 10, 10, now, stats));
        Assert.Equal(0u, conn.PlayerMana); Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.Equal(24, conn.Whisperer.Take(now + 4)); Assert.Equal(0, conn.Whisperer.Take(now + 5));
    }
    [Fact]
    public void Ward_precedes_physical_mana_mitigation_and_excludes_prevented_damage_from_recoup()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State;
        conn.PlayerMana = 60;
        conn.Ward.SetMaximum(50);
        conn.EnergyShield.SetMaximum(30, true);
        var stats = Program.CharacterPlayerStats(conn);
        double now = conn.Clock.Elapsed.TotalSeconds;
        Assert.Equal(20, Program.WhispererDamagePlayerHit(conn, 100, 10, 10, now, stats));
        Assert.Equal(40u, conn.PlayerMana);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.True(conn.Ward.Broken);
        Assert.Equal(14, conn.Whisperer.Take(now + 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_main_incoming_hit_routes_the_typed_packet_and_zeros_all_components_on_block(bool block)
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State;
        conn.Pools = conn.Pools with { Armour = 0, Evasion = 0, BlockChance = block ? 100 : 0,
            FireResistance = 0, ChaosResistance = 0, LifeGainedOnBlock = 0, ManaGainedOnBlock = 0 };
        conn.PlayerMana = 60;
        conn.EnergyShield.SetMaximum(30, true);
        conn.IncomingHitRandom = new Random(1);
        var attack = new MonsterAttack(0x102, 0.4, 10,
            DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(10, 10))
                .Add(DamageType.Chaos, new(10, 10)));
        var monster = new SpawnedMonster("typed contact", new(32, 32, 0), 0, 2, 1000) { Attack = attack };
        conn.Monsters[42] = monster;
        uint life = conn.PlayerLife;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var outcome = Program.CommitMonsterHitLocked("whisperer-main-hit", conn, 42, monster, attack,
            now, (32, 32), (32, 32), checkRange: false);
        Assert.NotNull(outcome);
        Assert.Equal(block, outcome.Blocked);
        Assert.Equal(life - (block ? 0u : 30u), conn.PlayerLife);
        Assert.Equal(block ? 60u : 0u, conn.PlayerMana);
        Assert.Equal(block ? 30u : 0u, conn.EnergyShield.Current);
        Assert.Equal(block ? 0 : 24, conn.Whisperer.Take(now + 4));
        Assert.Equal(0, conn.Whisperer.SpentRemainder);
    }

    [Fact]
    public async Task Real_paid_skill_cost_and_native_record_agree_and_only_paid_mana_grants_Rage()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame([17123, 4174, 1116, 22720, 540, 863]);
        var gem = ItemCatalog.Create(Program.Items.Find("Fireball")!);
        game.Items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Plate Vest")!) with
            { Sockets = [new(SocketColour.White, gem)] });
        var conn = game.State; conn.Clock.Stop();
        ushort skill = Program.GemSkillIdPool.Value[0];
        int raw = Program.Skills.EffectStats(Program.Skills.EffectRow("Fireball"), 1).First(v => v.Item1 == WKey("mana_cost")).Item2;
        var records = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", conn);
        Assert.Equal(raw * 2, records.Single(s => s.Id == skill).Stats!.First(s => s.Key == WKey("mana_cost")).Value);
        var pay = typeof(Program).GetMethod("PayMana", BindingFlags.NonPublic | BindingFlags.Static)!;
        uint before = conn.PlayerMana; int paid = 0;
        while (paid < 200)
        {
            object?[] args = [conn, skill, 0u]; Assert.True((bool)pay.Invoke(null, args)!);
            paid += (int)(uint)args[2]!;
        }
        Assert.Equal((uint)paid, before - conn.PlayerMana); Assert.Equal(10, WStat(conn, "current_rage"));
        int remainder = conn.Whisperer.SpentRemainder;
        conn.PlayerMana = 0;
        object?[] refused = [conn, skill, 0u]; Assert.False((bool)pay.Invoke(null, refused)!);
        Assert.Equal(remainder, conn.Whisperer.SpentRemainder);
        Assert.Null((await game.Change([863], false, refundPoints: true)).Error);
        Assert.Equal(0, conn.Whisperer.SpentRemainder);
    }
    [Fact]
    public void Partial_Ward_bypass_retains_eligible_physical_damage_for_Mana_before_Life()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State; conn.Ward.SetMaximum(100); conn.PlayerMana = 100;
        var stats = Program.CharacterPlayerStats(conn);
        stats[WKey("base_damage_bypass_ward_%")] = 50;
        double now = conn.Clock.Elapsed.TotalSeconds;
        Assert.Equal(50, Program.WhispererDamagePlayerHit(conn, 100, 100, 0, now, stats));
        Assert.Equal(50u, conn.PlayerMana);
        Assert.Equal(20, conn.Whisperer.Take(now + 4));
    }
    [Fact]
    public async Task Not_a_word_blocks_real_Blind_and_paid_refund_restores_incoming_effect()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame([9982, 5888]); var conn = game.State;
        Assert.False(Program.InflictPlayerBlind(conn, 4));
        Assert.Equal(conn.Pools.Accuracy, Program.PlayerAccuracy(conn));
        Assert.Null((await game.Change([5888], false, refundPoints: true)).Error);
        Assert.True(Program.InflictPlayerBlind(conn, 4));
        Assert.Equal(conn.Pools.Accuracy * 80 / 100, Program.PlayerAccuracy(conn));
        Assert.NotNull(Program.BlindPresentation(conn));
        Assert.Null((await game.Change([5888], true)).Error);
        Assert.Equal(conn.Pools.Accuracy, Program.PlayerAccuracy(conn));
        Assert.Equal(0, conn.Blind.Read(conn.Clock.Elapsed.TotalSeconds).Less);
    }

    [Fact]
    public async Task There_is_more_sets_effective_minimum_charges_and_refund_removes_their_bonuses()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame([21095, 2266]);
        Assert.Equal(4, WStat(game.State, "current_frenzy_charges"));
        Assert.Equal(4, WStat(game.State, "current_power_charges"));
        Assert.Null((await game.Change([2266], false, refundPoints: true)).Error);
        Assert.Equal(0, WStat(game.State, "current_frenzy_charges"));
        Assert.Equal(0, WStat(game.State, "current_power_charges"));
    }

    [Fact]
    public async Task Real_attack_fork_profile_has_two_forks_and_spell_profile_remains_unchanged()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame(WhispererBuilds[2]);
        int Get(string name) => WStat(game.State, name);
        var baseline = new GameServer.Core.Projectiles.ProjectileProfile("Whisperer fork") { Speed = 100 };
        Assert.Equal(2, GatewayProjectileModifiers.Resolve(baseline, Get, true).Fork);
        Assert.Equal(0, GatewayProjectileModifiers.Resolve(baseline, Get, false).Fork);
        Assert.Null((await game.Change([23799], false, refundPoints: true)).Error);
        Assert.Equal(0, GatewayProjectileModifiers.Resolve(baseline, Get, true).Fork);
    }

    [Fact]
    public void Native_hit_blind_projects_30_percent_and_keeps_a_weaker_smoke_source_separate()
    {
        if (!DataAvailable()) return;
        using var game = WhispererGame([9982, 5888]);
        var target = new SpawnedMonster("Blind target", new(50, 50, 0), 0, 2, 10000);
        game.State.Monsters[42] = target;
        double now = game.State.Clock.Elapsed.TotalSeconds;
        Assert.True(Program.InflictMonsterBlind(target, now, (100, WStat(game.State, "blind_effect_+%"), 4)));
        Assert.Equal(30, target.Blind.Read(now).Less);
        byte[] packet = Program.BlindPresentation(game.State)!;
        Assert.True(packet.AsSpan().IndexOf(InstanceEntities.StatsUpdate(42, [(973u, 1), (15023u, -30), (15024u, -30)])) >= 0);
        Assert.Equal(100, target.Blind.Apply(100, now + 4));
        game.State.FlaskGrounds.SetBlinded(42, true, () => 60000);
        Assert.Equal(70, ProgramValue<int>("MonsterBlindRating", game.State, 42u, target, 100, now + 4));
        byte[] smoke = Program.BlindPresentation(game.State)!;
        Assert.True(smoke.AsSpan().IndexOf(InstanceEntities.StatsUpdate(42, [(973u, 1), (15023u, -30), (15024u, -30)])) >= 0);
    }
}
