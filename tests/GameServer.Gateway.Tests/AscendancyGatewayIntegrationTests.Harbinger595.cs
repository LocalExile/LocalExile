using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public void Harbinger595_native_paths_and_dual_wield_server_timing_compose_action_and_Chill_once()
    {
        using var game = HarbingerGame();
        EquipFamilyGem(game, FamilyGem("Cleave"));
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        game.Items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        game.State.Pools = Program.PoolsFor(game.State, 60);
        var skill = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Cleave");
        var off = ProgramValue<SkillCatalog.Unarmed>("OffhandWeaponFor", game.State);
        double mainBefore = ProgramValue<double>("SupportedAttackSeconds", game.State, skill.Id);
        double offBefore = ProgramValue<double>("SupportedOffHandAttackSeconds", game.State, skill.Id, off);
        var before = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State)
            .Single(s => s.Id == SkillCatalog.FirstSkillId).Stats!.ToDictionary(s => s.Key, s => s.Value);
        game.State.PlayerStats[Program.Stats.Key(ActionSpeed.Reduction)!.Value] = -10;
        var cold = DamagePacket.Of(DamageType.Cold, 100, 100);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        Program.ApplyPlayerAilmentsLocked(game.State, cold, cold, false, false, now, Program.CharacterPlayerStats(game.State));
        double factor = 1.1 * game.State.ElementalAilments.ActionFactor(now);
        Assert.True(factor < 1.1);
        Assert.Equal(mainBefore / factor, ProgramValue<double>("SupportedAttackSeconds", game.State, skill.Id), 8);
        Assert.Equal(offBefore / factor, ProgramValue<double>("SupportedOffHandAttackSeconds", game.State, skill.Id, off), 8);
        var direct = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", game.State).ToDictionary(s => s.Key, s => s.Value);
        var native = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State)
            .Single(s => s.Id == SkillCatalog.FirstSkillId).Stats!.ToDictionary(s => s.Key, s => s.Value);
        uint action = Program.Stats.Key(ActionSpeed.Virtual)!.Value;
        Assert.Equal((int)Math.Round(100 * (factor - 1)), direct[action]);
        Assert.Equal(direct[action], native[action]); // PlayerSkills directly calls FinalPlayerStatsFrom.
        Assert.InRange(native[StatKeys.HundredTimesAttacksPerSecond],
            (int)Math.Round(before[StatKeys.HundredTimesAttacksPerSecond] * factor) - 1,
            (int)Math.Round(before[StatKeys.HundredTimesAttacksPerSecond] * factor) + 1);
        Assert.Equal(before[Program.Stats.Key("off_hand_accuracy_rating")!.Value], native[Program.Stats.Key("off_hand_accuracy_rating")!.Value]);
    }

    [Fact]
    public async Task Harbinger595_independent_hybrid_budgets_publish_current_BASE_seconds_and_recovery_words()
    {
        using var game = HarbingerGame();
        var use = FlaskRecovery.For(new(100, 80, 4, 1, 20), new(1, ["Base", "Flask"], 1),
            new Dictionary<string, int>(), n => n == "flask_mana_recovery_rate_+%" ? 100 : 0, 1, 1000, 60);
        ushort next = 100;
        var effects = Flasks.RecoveryEffects(use, 0, 0, () => next++, 113, 114);
        Assert.Equal(new[] { 4d, 2d }, effects.Select(e => e.Ends - e.Started));
        foreach (var e in effects) game.State.FlaskEffects.Start(e);
        var recovered = game.State.FlaskEffects.Advance(0, 1, manaRecoveryRate: 1.5, expiryRate: .5);
        Assert.Equal(25, recovered.Life);
        Assert.Equal(60, recovered.Mana);
        Assert.Equal(3.5, game.State.FlaskEffects.Remaining(effects[0]));
        Assert.Equal(1.5, game.State.FlaskEffects.Remaining(effects[1]));
        game.State.PlayerStats[Program.Stats.Key("buff_time_passed_+%")!.Value] = -50;
        await using var wire = new ProjectileRuntimeFixture(game.State);
        foreach (var e in effects)
        {
            Assert.True(await ProgramValue<Task<bool>>("SendRecoveryFlaskStart", "hybrid", wire.Stream, wire.Ciphers, game.State, e, true));
            var bytes = await wire.ReadWire();
            var (stats, prefix) = HarbingerStatPrefix(bytes);
            Assert.Equal(-50, stats[Program.Stats.Key("buff_time_passed_+%")!.Value]);
            var expected = Flasks.Buff(Program.PlayerId, e.Buff, e.BuffRow, e.Slot,
                game.State.FlaskEffects.Remaining(e), [(uint)Math.Round((e.LifePerSecond + e.ManaPerSecond) * 60)]);
            Assert.Equal(expected, bytes.AsSpan(prefix).ToArray());
        }
        var ended = game.State.FlaskEffects.Advance(1, 3, manaRecoveryRate: 1.5, expiryRate: 1);
        Assert.Single(ended.Ended);
        Assert.Same(effects[1], ended.Ended[0]);
        Assert.Equal(1.5, game.State.FlaskEffects.Remaining(effects[0]));
        Assert.Equal(0, game.State.FlaskEffects.Remaining(effects[1]));
    }

    [Theory]
    [InlineData((int)ChargeType.Endurance, "brutal")] [InlineData((int)ChargeType.Frenzy, "affliction")]
    [InlineData((int)ChargeType.Power, "absorption")]
    public async Task Harbinger595_converted_charge_mirrors_keep_generic_clock_and_BASE_packet(int chargeType, string special)
    {
        var type = (ChargeType)chargeType;
        using var game = HarbingerGame([11043, 18859, 32223, 41042]);
        game.State.PlayerStats[Program.Stats.Key(Charges.ConversionFlag(type))!.Value] = 1;
        game.State.PlayerStats[Program.Stats.Key("buff_time_passed_+%")!.Value] = -50;
        _ = Program.CharacterPlayerStats(game.State);
        var session = ProgramValue<ChargeSession>("ChargesOf", game.State);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        session.State.SetExpiryRate(now, .5);
        session.State.Gain(type, 2, now, session.Limits(type));
        await using var wire = new ProjectileRuntimeFixture(game.State);
        Assert.True(await ProgramValue<Task<bool>>("SendChargeBuffs", "converted", wire.Stream, wire.Ciphers, game.State, session));
        var bytes = await wire.ReadWire();
        var (stats, prefix) = HarbingerStatPrefix(bytes);
        Assert.Equal(.5, session.State.ExpiryRate); // Buff-only -30/debuff-only +100 do not affect category3.
        Assert.Equal(0, stats[Program.Stats.Key($"actual_current_{Charges.Name(type)}_charges")!.Value]);
        Assert.Equal(2, stats[Program.Stats.Key($"current_{special}_charges")!.Value]);
        var expected = ChargeBuffs.Add(type, (ushort)(game.State.NextBuff - 1), 2, 10, Program.PlayerId, converted: true);
        Assert.Equal(expected[..23], bytes.AsSpan(prefix, 23).ToArray());
        float budget = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(prefix + 23, 4)));
        Assert.InRange(budget, 9.5f, 10f);
    }

    [Fact]
    public void Harbinger595_Mana_leech_applies_recovery_once_and_loading_discard_keeps_active_source()
    {
        using var game = HarbingerGame([24174, 20105, 53334, 62155]);
        game.State.PlayerMana = 0;
        var stats = Program.CharacterPlayerStats(game.State);
        Assert.Equal(50, stats[Program.Stats.Key("mana_recovery_rate_+%")!.Value]);
        var session = Program.LeechOf(game.State);
        double now = Program.ProjectileWorldTime(game.State);
        session.Mana.Grant(now, 1000, game.State.Pools.Mana, 1000);
        Program.DiscardLoadingRecovery(game.State, now + .5);
        Assert.Equal(0, session.TakeRecovery(now + .5).Mana);
        Assert.Equal(game.State.Pools.Mana * .02 * 1.5 * .25, session.TakeRecovery(now + .75).Mana, 8);
        Assert.True(session.Mana.Active(now + .75));
        Assert.Equal(0u, game.State.PlayerMana);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger595_queued_flask_gate_rejects_retirement_before_stat_bookkeeping(bool recovery)
    {
        using var game = HarbingerGame();
        await using var wire = new ProjectileRuntimeFixture(game.State);
        var utility = HarbingerSilver(game);
        var effect = new Flasks.Effect(0, 100, 113, 10, 0, 0, 10);
        game.State.FlaskEffects.Start(effect);
        await wire.HoldOutput();
        Task<bool> queued = recovery
            ? ProgramValue<Task<bool>>("SendRecoveryFlaskStart", "retire", wire.Stream, wire.Ciphers, game.State, effect, true)
            : HarbingerSilverStart(wire, utility);
        Assert.False(queued.IsCompleted);
        var sent = game.State.SentStatKeys;
        game.State.Retire();
        wire.ReleaseOutput();
        Assert.False(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(sent, game.State.SentStatKeys);
        Assert.Empty(await wire.ReadWire());
    }
}
