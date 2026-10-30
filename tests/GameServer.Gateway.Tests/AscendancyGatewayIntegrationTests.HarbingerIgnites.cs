using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static MonsterIgnites.Settings HarbingerIgnite(double seconds = 4, double dps = 100) =>
        new(100, dps, dps, dps, dps, seconds);

    static void HarbingerTargetCurse(ClientState conn, double now, double seconds, int other, bool aura = false)
    {
        conn.TemporalChains[2] = new(40, 1, new EffectClock(seconds, now), -15, -9, other, aura);
        Program.SetMonsterCurseSpeed(conn, 2, now);
    }

    static Task HarbingerBurn(ProjectileRuntimeFixture wire) => ProgramValue<Task>("BurnIgnited",
        "harbinger-ignite", wire.Stream, wire.Ciphers, wire.Conn);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_target_Ignite_pays_500_once_after_curse_removal_or_late_natural_expiry(bool natural)
    {
        using var game = HarbingerGame();
        var conn = game.State;
        var target = new SpawnedMonster("burning", new(32, 32, 0), 0, 1, 1000);
        conn.Monsters[2] = target;
        await using var wire = new ProjectileRuntimeFixture(conn);
        HarbingerTargetCurse(conn, 0, natural ? 2 : double.PositiveInfinity, -50, aura: !natural);
        Program.IgniteOnHitLocked(wire.Session, 2, target, HarbingerIgnite(), true, 0);
        if (!natural)
        {
            wire.Advance(2);
            conn.TemporalChains.Remove(2); // The same reconciliation hook used by actual aura removal.
            Program.SetMonsterCurseSpeed(conn, 2, 2);
            Assert.True(target.Ignites.HasPendingDamage);
            Assert.Equal(1000u, target.Life); // The hook did not discard or independently apply the owed damage.
        }
        wire.Advance(5);
        await HarbingerBurn(wire);
        Assert.Equal(500u, target.Life); // 2 s at half expiry rate, then 3 s at normal rate; DPS stays 100.
        Assert.False(target.Ignites.Burning);
        Assert.False(target.Ignites.HasPendingDamage);
        await HarbingerBurn(wire);
        Assert.Equal(500u, target.Life);
        Assert.Equal(natural, conn.TemporalChains.ContainsKey(2)); // An unreconciled expired entry cannot slow it.
    }

    [Fact]
    public void Harbinger_target_Ignite_replacement_keeps_strongest_fallback_mitigation_and_fractional_damage()
    {
        var ignite = new MonsterIgnites();
        var random = new Random(1);
        ignite.ChangeRate(0, .5, 2, .333);
        Assert.True(ignite.TryApply(HarbingerIgnite(4, 100), true, 0, 0, false, random));
        Assert.True(ignite.TryApply(HarbingerIgnite(8, 10), true, 0, 0, false, random));
        ignite.ChangeRate(2, .25, 4, .333); // Replacement reconciles 200 * .333, retaining .6 of a point.
        Assert.Equal(66, ignite.Advance(2, .333));
        Assert.Equal(0, ignite.Advance(2, .333)); // No wall-clock progress (pause) produces no extra payment.
        Assert.Equal(2.5, ignite.Remaining(4) - 4, 8); // Weaker has 6.5 base seconds; stronger has 2.5.
        Assert.Equal(10.5, ignite.Until, 8);
        // The replacement ends at 4 even if the next reader arrives only at 7.
        Assert.Equal(152, ignite.Advance(7, .333)); // 450 strong +5 fallback, mitigated, plus .6 carry.
        Assert.Equal(11, ignite.Advance(10.5, .333)); // Remaining weaker 35 * .333 plus carried fraction.
        Assert.False(ignite.Burning);
        Assert.Equal(0, ignite.Advance(100, .333));
    }

    [Theory]
    [InlineData("partial")] [InlineData("expiry")] [InlineData("rate")]
    [InlineData("death")] [InlineData("replace")] [InlineData("identity")] [InlineData("epoch")]
    public async Task Harbinger_target_Ignite_held_start_uses_gate_time_prefix_BASE_budget_and_original_target(string change)
    {
        using var game = HarbingerGame();
        var conn = game.State;
        const double start = 100; // The fixture's manual projectile world leads the live (unpaused) connection clock.
        var target = new SpawnedMonster("burning", new(32, 32, 0), 0, 1, 2000);
        conn.Monsters[2] = target;
        await using var wire = new ProjectileRuntimeFixture(conn);
        HarbingerTargetCurse(conn, start, 2, -50);
        await wire.HoldOutput();
        Program.IgniteOnHitLocked(wire.Session, 2, target, HarbingerIgnite(), true, start);
        ushort original = target.Ignites.Buff!.Value;
        // Only publication is waiting. Neither the burn writer nor its Advance runs while this start waits.
        wire.Advance(start + (change == "expiry" ? 6 : 3));
        double at = Program.ProjectileWorldTime(conn);
        if (change == "death") { target.Dead = true; target.Life = 0; }
        else if (change == "identity") conn.Monsters[2] = ProjectileRuntimeFixture.Monster(32, 32);
        else if (change == "epoch") conn.ProjectileEpoch++;
        else if (change == "rate") HarbingerTargetCurse(conn, at, 10, -75);
        else if (change == "replace")
            Program.IgniteOnHitLocked(wire.Session, 2, target, HarbingerIgnite(2), true, at);
        wire.ReleaseOutput();
        byte[] bytes;
        if (change == "epoch")
        {
            wire.Session.Invalidate();
            await wire.Session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            bytes = await wire.ReadWire();
        }
        else bytes = await wire.Read();
        if (change is "death" or "identity" or "epoch") Assert.Empty(bytes);
        else
        {
            if (change == "replace")
            {
                var remove = InstanceEntities.RemoveBuff(2, original);
                Assert.Equal(remove, bytes[..remove.Length]);
                bytes = bytes[remove.Length..];
            }
            // Reuse the compact-stat decoder while checking this prefix's actual target id separately.
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4)));
            var decoded = bytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(decoded.AsSpan(2, 4), Program.PlayerId);
            var (stats, end) = HarbingerStatPrefix(decoded);
            Assert.Equal(change == "rate" ? -75 : 0,
                stats[Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value]);
            Assert.Equal(change == "rate" ? -15 : 0, stats[Program.Stats.Key(ActionSpeed.Virtual)!.Value]);
            if (change == "expiry") Assert.Equal(bytes.Length, end); // Current zero clears survive without a late add.
            else
            {
                double remaining = change == "replace" ? 2 : 4 - 2 * .5 - (at - start - 2);
                Assert.Equal(InstanceEntities.AddBuffId, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(end, 2)));
                Assert.Equal(change == "replace" ? target.Ignites.Buff!.Value : original,
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(end + 12, 2)));
                Assert.Equal(remaining, BitConverter.UInt32BitsToSingle(
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(end + 23, 4))), 6);
            }
        }
        if (change is "partial" or "expiry" or "rate" or "replace")
        {
            Assert.Equal(2000u, target.Life); // Pure gate projection never pays, consumes or clears owed damage.
            wire.Advance(start + 20);
            await HarbingerBurn(wire);
            uint life = change == "rate" ? 900u : 1500u; // 11 s with replacement curse, otherwise 5 s; DPS stays 100.
            Assert.Equal(life, target.Life);
            await HarbingerBurn(wire);
            Assert.Equal(life, target.Life);
        }
    }

    [Fact]
    public async Task Harbinger_companion_admission_does_not_bypass_real_gem_weapon_requirements_or_spend_Mana()
    {
        using var game = HarbingerGame([24174, 20105]);
        var conn = game.State;
        var grant = Program.Harbingers.ForNode(20105)!;
        var companion = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", conn).Single(g => g.Effect == grant.SummonEffect);
        Assert.Null(ProgramValue<string?>("GemSkillInstanceDisabled", conn, companion.Effect, companion.Id));
        Assert.Equal("skill is unavailable", ProgramValue<string?>("GemSkillInstanceDisabled", conn,
            Program.Skills.EffectRow("BurningArrow"), companion.Id));
        EquipFamilyGem(game, FamilyGem("Burning Arrow"));
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", conn).Single(g => g.Name == "Burning Arrow");
        Assert.Equal("the equipped weapon cannot use this skill",
            ProgramValue<string?>("GemSkillInstanceDisabled", conn, gem.Effect, gem.Id));
        await using var wire = new ProjectileRuntimeFixture(conn);
        uint mana = conn.PlayerMana;
        int action = conn.ActionCount;
        Assert.False(await ProgramValue<Task<bool>>("CastGemSkill", "restricted-harbinger", wire.Stream,
            wire.Ciphers, conn, gem.Id, (32, 32), (40, 32), 0u, (ushort)0, conn.ActionCount, null, false));
        Assert.Equal(mana, conn.PlayerMana);
        Assert.Equal(action, conn.ActionCount);
        Assert.Empty(wire.Session.Runtime.Projectiles);
    }
}
