using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static (Dictionary<uint, int> Stats, int End) HarbingerStatPrefix(byte[] bytes)
    {
        var reader = new LoginReader(bytes);
        Assert.Equal(0x1b3, reader.U16()); Assert.Equal(Program.PlayerId, reader.U32());
        Assert.Equal(0u, reader.U32()); Assert.Equal(0, reader.U16());
        static uint Compact(ref LoginReader r, bool signed = false)
        {
            byte first = r.U8();
            if (first >= 0xf0) return r.U32();
            int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : 3;
            int bits = 7 + following * 7;
            uint value = (uint)(first & (0x7f >> following));
            for (int i = 0; i < following; i++) value = value << 8 | r.U8();
            if (signed && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
            return value;
        }
        uint count = Compact(ref reader);
        var stats = new Dictionary<uint, int>();
        for (uint i = 0; i < count; i++) stats.Add(Compact(ref reader), unchecked((int)Compact(ref reader, true)));
        Assert.False(reader.Short);
        return (stats, reader.Position);
    }

    [Fact]
    public async Task Harbinger_expired_unreconciled_owner_curse_is_ignored_without_mutating_its_clock_or_instance()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame();
        double now = game.State.Clock.Elapsed.TotalSeconds;
        var curse = new TemporalChainsState(99, 1, new EffectClock(.05, now, .5), -7, -4, -12, false);
        game.State.SelfTemporalChains = curse;
        var stale = new Dictionary<uint, int>
        {
            [Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value] = -60,
            [Program.Stats.Key("debuff_time_passed_+%")!.Value] = 100,
        };
        double remaining = curse.Clock.Remaining, rate = curse.Clock.Rate, deadline = curse.Clock.Deadline;
        await Task.Delay(200);
        Assert.True(curse.Clock.Deadline < game.State.Clock.Elapsed.TotalSeconds);
        Assert.Equal(1, ProgramValue<double>("RecipientEffectRate", game.State, stale, 1));
        Assert.Equal(remaining, curse.Clock.Remaining);
        Assert.Equal(rate, curse.Clock.Rate);
        Assert.Equal(deadline, curse.Clock.Deadline);
        Assert.Equal((ushort)99, curse.Instance);
        Assert.Same(curse, game.State.SelfTemporalChains);
    }

    [Theory]
    [InlineData("rate")] [InlineData("death")] [InlineData("area")] [InlineData("pause")] [InlineData("replacement")] [InlineData("expiry")]
    public async Task Harbinger_taunt_replacement_uses_current_recipient_and_source_at_accepted_gate(string change)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame();
        var monster = new SpawnedMonster("taunt recipient", new(32, 32, 0), 0, 1, 100) { Hostile = true };
        game.State.Monsters[2] = monster;
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.TemporalChains[2] = new(31, 1, new EffectClock(change == "expiry" ? .2 : 20, now), -15, -9, -25, false);
        var ground = new FlaskGrounds.Ground(FlaskGrounds.Kind.Taunt, 32, 32, 30, now + 3);
        ushort firstBuff = game.State.NextBuff++;
        ground.Taunted[2] = firstBuff; ground.TauntRecipients[2] = monster; ground.TauntClocks[2] = new(3, now, .75);
        game.State.FlaskGrounds.Add(ground);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        Task<bool> Publish() => ProgramValue<Task<bool>>("SendTauntBuffs", "taunt-clock", wire.Stream,
            wire.Ciphers, game.State, ground);
        Assert.True(await Publish());
        var firstPublication = await wire.ReadWire();
        AssertTauntRecoveryPayload(firstPublication, firstBuff);
        await wire.HoldOutput();
        var queued = Publish(); Assert.False(queued.IsCompleted);
        if (change == "rate") game.State.TemporalChains.Remove(2);
        else if (change == "expiry") await Task.Delay(350); // The old entry deliberately remains in the dictionary.
        else if (change == "death") { monster.Dead = true; monster.Life = 0; }
        else if (change == "area") game.State.LeavingArea = true;
        else if (change == "replacement") game.State.Monsters[2] = new("replacement", new(32, 32, 0), 0, 1, 100);
        else game.State.Clock.Stop();
        double releasedAt = game.State.Clock.Elapsed.TotalSeconds;
        wire.ReleaseOutput();
        Assert.Equal(change is "rate" or "death" or "replacement" or "expiry", await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        game.State.LeavingArea = false; game.State.Clock.Start();
        var bytes = await wire.ReadWire();
        if (change is "area" or "pause") { Assert.Empty(bytes); return; }
        if (change is "death" or "replacement")
        {
            Assert.Empty(ground.Taunted);
            Assert.Equal(InstanceEntities.RemoveBuff(2, firstBuff), bytes);
            return;
        }
        uint key = Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value;
        int prefix = bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(2, [(key, 0)]));
        int add = bytes.AsSpan().IndexOf(InstanceEntities.AddBuff(2, ground.Taunted[2], 408, Program.PlayerId)[..14]);
        Assert.True(prefix >= 0 && add > prefix);
        float baseSeconds = BitConverter.UInt32BitsToSingle(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(add + 23, 4)));
        Assert.InRange(baseSeconds, 3 - (releasedAt - now) * .75 - .15, 3 - (releasedAt - now) * .75 + .01);
        Assert.Equal(1, ground.TauntClocks[2].Rate);
        AssertTauntRecoveryPayload(bytes, ground.Taunted[2]);
    }

    static void AssertTauntRecoveryPayload(byte[] bytes, ushort instance)
    {
        int at = bytes.AsSpan().IndexOf(InstanceEntities.AddBuff(2, instance, 408, Program.PlayerId)[..14]);
        Assert.True(at >= 0);
        var reader = new LoginReader(bytes.AsSpan(at).ToArray());
        Assert.Equal(0x1c1, reader.U16()); Assert.Equal(2u, reader.U32());
        Assert.Equal(0u, reader.U32()); Assert.Equal(0, reader.U16());
        Assert.Equal(instance, reader.U16()); Assert.Equal(408, reader.U16());
        Assert.Equal(Program.PlayerId, reader.U32()); Assert.Equal(0, reader.U16());
        Assert.Equal(2, reader.U8()); // finite base budget
        Assert.InRange(BitConverter.UInt32BitsToSingle(reader.U32()), 0, 3);
        // Pinned IsRecovery row 408: GrantedStats taunt_target_id, is_taunted.
        Assert.Equal(Program.PlayerId, reader.U32()); Assert.Equal(1u, reader.U32());
        Assert.Equal(0u, reader.U32()); // trailing vector count, after BOTH recovery words
        Assert.False(reader.Short);
        Assert.Equal(bytes.Length - at, reader.Position);
    }

    [Theory]
    [InlineData("rate", false)] [InlineData("rate", true)]
    [InlineData("death", false)] [InlineData("area", false)] [InlineData("pause", false)] [InlineData("expiry", false)]
    public async Task Harbinger_charge_replacement_uses_current_stats_and_lifecycle_at_accepted_gate(string change, bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859], ruthless);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.SelfTemporalChains = new(50, 1, new EffectClock(20, now), -7, -4, -12, false);
        var session = ProgramValue<ChargeSession>("ChargesOf", game.State);
        _ = Program.CharacterPlayerStats(game.State);
        var limits = change == "expiry" ? new Charges.Limits(3, 0, 300) : session.Limits(ChargeType.Endurance);
        session.State.Gain(ChargeType.Endurance, 1, now, limits);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        Task<bool> Publish() => ProgramValue<Task<bool>>("SendChargeBuffs", "charge-clock", wire.Stream,
            wire.Ciphers, game.State, session);
        Assert.True(await Publish());
        var first = await wire.ReadWire();
        Assert.Equal(-12, HarbingerStatPrefix(first).Stats[Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value]);
        Assert.Equal(.88, session.State.ExpiryRate);
        await wire.HoldOutput();
        if (change == "expiry") session.State.Gain(ChargeType.Endurance, 1, game.State.Clock.Elapsed.TotalSeconds, limits);
        var queued = Publish(); Assert.False(queued.IsCompleted);
        if (change == "rate") game.State.SelfTemporalChains = null;
        else if (change == "expiry") await Task.Delay(500);
        else if (change == "death") game.State.PlayerLife = 0;
        else if (change == "area") game.State.LeavingArea = true;
        else game.State.Clock.Stop();
        double releasedAt = game.State.Clock.Elapsed.TotalSeconds;
        wire.ReleaseOutput();
        Assert.Equal(change is not ("area" or "pause"), await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        game.State.LeavingArea = false; game.State.Clock.Start();
        var bytes = await wire.ReadWire();
        if (change is "area" or "pause") { Assert.Empty(bytes); return; }
        if (change is "death" or "expiry")
        {
            Assert.Equal(0, session.State.Held(ChargeType.Endurance));
            Assert.True(bytes.AsSpan().IndexOf(InstanceEntities.AddBuff(Program.PlayerId, 1, 28, Program.PlayerId)[..6]) < 0);
            return;
        }
        Assert.Equal(1, session.State.ExpiryRate);
        double budget = session.State.RemainingBase(ChargeType.Endurance, game.State.Clock.Elapsed.TotalSeconds);
        Assert.InRange(budget, 9, 10);
        Assert.Equal(0, HarbingerStatPrefix(bytes).Stats[Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value]);
        var packet = ChargeBuffs.Add(ChargeType.Endurance, (ushort)(game.State.NextBuff - 1), 1, 0, Program.PlayerId);
        var actual = bytes.AsSpan(bytes.Length - packet.Length);
        Assert.True(actual[..23].SequenceEqual(packet.AsSpan(0, 23)));
        float baseSeconds = BitConverter.UInt32BitsToSingle(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(actual.Slice(23, 4)));
        // The pending native reissue preserves the independently predicted consumed .88 budget, rather than wall TTL.
        Assert.InRange(baseSeconds, 10 - (releasedAt - now) * .88 - .15, 10 - (releasedAt - now) * .88 + .01);
        Assert.InRange(baseSeconds - budget, -.01, .15);
    }

    [Theory]
    [InlineData("rate")] [InlineData("refund")] [InlineData("death")] [InlineData("area")] [InlineData("replacement")]
    public async Task Harbinger_queued_buff_uses_current_base_budget_and_rechecks_live_source_at_send_gate(string change)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([24174, 20105]);
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var source = Program.Harbingers.ForNode(20105)!;
        var now = DateTimeOffset.UtcNow;
        roster.SetRecipientRate(game.State.SummonLease, now, .7);
        roster.Activate(game.State.SummonLease, source, 100, now);
        roster.Step(game.State.SummonLease, now.AddSeconds(4), true, false);
        roster.Step(game.State.SummonLease, now.AddMilliseconds(4700), true, false);
        var buff = Assert.Single(roster.Buffs(game.State.SummonLease, now.AddMilliseconds(4700)));
        const uint actorId = 42;
        game.State.Monsters[actorId] = new("Arcane", new(32, 32, 0), 0, (int)actorId, 1) { Companion = source };
        game.State.CompanionObjects[buff.Owner.Id] = actorId;
        game.State.SentStatKeys.Add(13224); // The native client still holds the previously allocated -30 source.
        await fixture.HoldOutput();
        var pending = ProgramValue<Task<bool>>("SendCompanionBuff", "harbinger-test", fixture.Stream,
            fixture.Ciphers, game.State, buff, actorId, (ushort)17, game.State.ProjectileEpoch);
        if (change == "rate") roster.SetRecipientRate(game.State.SummonLease, now.AddMilliseconds(5700), 1);
        else if (change == "refund") game.State.MainAscendancy = game.State.MainAscendancy with { Nodes = [24174] };
        else if (change == "death") game.State.PlayerLife = 0;
        else if (change == "area") game.State.LeavingArea = true;
        else roster.Activate(game.State.SummonLease, source, 100, now.AddMilliseconds(5700));
        fixture.ReleaseOutput();
        Assert.Equal(change == "rate", await pending);
        game.State.LeavingArea = false; // Allow the fixture's output sentinel after verifying the rejected area send.
        var bytes = await fixture.Read();
        if (change == "rate")
        {
            var (native, end) = HarbingerStatPrefix(bytes);
            Assert.Equal(0, native[13224]); // Clear precedes construction, including the resource-pump caller.
            Assert.Equal(0x1c1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(end, 2)));
            float budget = BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(end + 23, 4)));
            Assert.Equal(3.3, budget, 6);
            Assert.Equal(roster.Buffs(game.State.SummonLease, now.AddMilliseconds(5700))[0].Buff.EndsAt,
                game.State.CompanionBuffs[buff.Buff.Skill.Effect].Ends);
        }
        else { Assert.Empty(bytes); Assert.Empty(game.State.CompanionBuffs); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_actual_Enduring_Cry_category_one_lifetime_survives_carry_and_paid_timeflow_refund(bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame(HarbingerBuilds[0], ruthless);
        game.State.PlayerStats[Program.Stats.Key("warcry_speed_+%")!.Value] = 10000;
        await game.Command("/set_stat base_strength 100");
        EquipFamilyGem(game, FamilyGem("Enduring Cry") with { Quality = 0 });
        game.State.Monsters[2] = new("power", new(32, 32, 0), 0, 2, 100)
            { Rarity = MonsterRarity.Unique, Hostile = true };
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Enduring Cry");
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        var cooldown = ProgramValue<SingleUseCooldown?>("CooldownFor", game.State, gem.Effect, gem.Level, gem.Quality);
        Assert.True(Program.TryBeginSkillCooldownUse(game.State, gem.Id, gem.Effect, cooldown, false,
            () => game.State.ActionCount++, out var ticket));
        double before = game.State.Clock.Elapsed.TotalSeconds;
        Assert.True(await ProgramValue<Task<bool>>("CastGemSkill", "harbinger-test", fixture.Stream,
            fixture.Ciphers, game.State, gem.Id, (32, 32), (32, 32), 0u, (ushort)0,
            game.State.ActionCount, ticket, false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (game.State.EnduringCryRecovery.Until == 0) await Task.Delay(5, timeout.Token);
        game.State.Clock.Stop();
        double now = game.State.Clock.Elapsed.TotalSeconds;
        Assert.InRange(game.State.EnduringCryRecovery.Until, before + 25d / 7, now + 25d / 7);
        double rate = game.State.PlayerMaxLife * .08;
        Assert.Equal(rate, game.State.EnduringCryRecovery.Rate(now), 8);
        var carried = game.State.EnduringCryRecovery.Carry(now);
        Assert.Equal(.7, carried.ExpiryRate);
        Program.CarryAntiquarian(game.State, 0);
        var next = game.Reload(); next.Clock.Stop();
        next.PlayerStats[Program.Stats.Key("base_strength")!.Value] = 100;
        Program.RestoreAntiquarian(next);
        double arrived = next.Clock.Elapsed.TotalSeconds;
        Assert.Equal(arrived + carried.Remaining / .7, next.EnduringCryRecovery.Until, 5);
        Assert.Equal(rate, next.EnduringCryRecovery.Rate(arrived), 8);
        var packet = Program.AntiquarianPresentation(next)!;
        Assert.NotNull(packet); // The native timer carries base seconds; its own recipient rate supplies wall time.
        Assert.Null((await game.Change([41042], allocate: false, refundPoints: true, state: next)).Error);
        _ = Program.CharacterPlayerStats(next);
        Assert.Equal(arrived + carried.Remaining, next.EnduringCryRecovery.Until, 5);
        Assert.NotNull(Program.AntiquarianPresentation(next));
        Assert.Equal(carried.Pending + carried.Remaining * rate, next.EnduringCryRecovery.Take(arrived + 10), 5);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_Focus_receipt_survives_actual_paid_position_use_and_held_update_frames_until_stop(bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859], ruthless);
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Scorching Ray"));
        var skill = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Scorching Ray");
        game.State.PlayerMana = game.State.Pools.Mana;
        uint before = game.State.PlayerMana;
        await game.Dispatch(new LoginWriter().U16(0x25).U32(32).U32(32).U16(skill.Id).U16(0).U16(0x409).ToArray());
        Assert.True(game.State.PlayerMana < before);
        Assert.Equal((skill.Id, game.State.ActionCount, game.State.ChannelGeneration), game.State.HarbingerChannel);
        await game.Dispatch(new LoginWriter().U16(0x197).U32(32).U32(32).ToArray());
        Assert.True(ProgramValue<bool>("Channelling", game.State));
        await game.Dispatch(new LoginWriter().U16(0x199).ToArray());
        Assert.False(ProgramValue<bool>("Channelling", game.State));
        game.State.PlayerMana = 0;
        await game.Dispatch(new LoginWriter().U16(0x25).U32(32).U32(32).U16(skill.Id).U16(0).U16(0x409).ToArray());
        Assert.False(ProgramValue<bool>("Channelling", game.State));
    }

    [Fact]
    public async Task Harbinger_Focus_channel_gate_requires_actual_payment_current_action_and_live_channel_updates()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859]);
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Scorching Ray"));
        var skill = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Scorching Ray");
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        game.State.ChannelSkill = skill.Id;
        game.State.LastChannelUpdateAt = game.State.Clock.Elapsed.TotalSeconds;
        game.State.PlayerMana = 0;
        Task<bool> Cast() => ProgramValue<Task<bool>>("CastGemSkill", "harbinger-test", fixture.Stream,
            fixture.Ciphers, game.State, skill.Id, (32, 32), (32, 32), 0u, (ushort)0,
            game.State.ActionCount, null, false);
        Assert.False(await Cast());
        Assert.False(ProgramValue<bool>("Channelling", game.State));
        game.State.PlayerMana = game.State.Pools.Mana;
        Assert.True(await Cast());
        Assert.True(ProgramValue<bool>("Channelling", game.State));
        game.State.ChannelGeneration++;
        Assert.False(ProgramValue<bool>("Channelling", game.State));
        Assert.True(await Cast());
        Assert.True(ProgramValue<bool>("Channelling", game.State));
        game.State.LastChannelUpdateAt = game.State.Clock.Elapsed.TotalSeconds - 1.01;
        Assert.False(ProgramValue<bool>("Channelling", game.State));
        game.State.LastChannelUpdateAt = game.State.Clock.Elapsed.TotalSeconds;
        game.State.ActionCount++;
        Assert.False(ProgramValue<bool>("Channelling", game.State));
    }

    [Fact]
    public async Task Harbinger_received_curse_rate_refund_republishes_native_remaining_time_without_restarting_budget()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859, 32223, 41042, 58661, 34822, 63336, 39702]);
        await game.Command("/set_stat base_dexterity 100");
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Temporal Chains"));
        game.State.PlayerStats[Program.Stats.Key("base_cast_speed_+%")!.Value] = 9900;
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Temporal Chains");
        game.State.Monsters[2] = new("Atziri", new(32, 32, 1), 0, 1, 100)
            { Variety = Program.Monsters.Find("Metadata/Monsters/Atziri/Atziri"), Rarity = MonsterRarity.Unique };
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        await ProgramValue<Task>("CastTemporalChains", "harbinger-test", fixture.Stream, fixture.Ciphers,
            game.State, gem.Id, (32, 32), game.State.ActionCount, game.State.ProjectileEpoch);
        game.State.Clock.Stop();
        _ = Program.CharacterPlayerStats(game.State);
        var oldRecoverySnapshot = Program.CharacterPlayerStats(game.State);
        var curse = Assert.IsType<TemporalChainsState>(game.State.SelfTemporalChains);
        double remaining = curse.Clock.Remaining, now = game.State.Clock.Elapsed.TotalSeconds;
        ushort previous = curse.Instance;
        Assert.Equal(2, curse.Clock.Rate);
        Assert.Null((await game.Change([41042], allocate: false, refundPoints: true)).Error);
        game.State.Clock.Start();
        await RefreshCurses(fixture);
        game.State.Clock.Stop();
        Assert.Equal(0, HarbingerStat(game.State, "debuff_time_passed_+%"));
        Assert.Equal(1, curse.Clock.Rate);
        Assert.True(previous != curse.Instance, $"old={previous}, new={curse.Instance}, next={game.State.NextBuff}, deadline={curse.Clock.Deadline}, presented={curse.PresentedDeadline}");
        Assert.InRange(curse.Clock.Remaining, remaining - (game.State.Clock.Elapsed.TotalSeconds - now) - 1e-8, remaining + 1e-8);
        Assert.Equal(now + remaining, curse.PresentedDeadline, 8);
        Assert.Equal(curse.Clock.Deadline, curse.PresentedDeadline);
        ushort stable = curse.Instance;
        for (int i = 0; i < 10; i++)
        {
            // The resource pump may retain its previous 250 ms recovery snapshot after a paid refund.
            _ = ProgramValue<double>("RecipientEffectRate", game.State, oldRecoverySnapshot, 4);
            Assert.Equal(1, curse.Clock.Rate);
            await RefreshCurses(fixture);
            Assert.Equal(stable, curse.Instance);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_selected_Focus_clears_and_prevents_upstream_ordinary_ailments_then_refund_removes_immunity(bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859], ruthless);
        var hit = GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Fire, 100, 100)
            .Add(GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Cold, 100, 100))
            .Add(GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Lightning, 100, 100));
        void Inflict() => Program.ApplyPlayerAilmentsLocked(game.State, hit, hit, true, false,
            game.State.Clock.Elapsed.TotalSeconds, Program.CharacterPlayerStats(game.State));
        Inflict();
        Assert.True(game.State.ElementalAilments.Snapshot(game.State.Clock.Elapsed.TotalSeconds).Ignite);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var now = DateTimeOffset.UtcNow;
        roster.Activate(game.State.SummonLease, Program.Harbingers.ForNode(18859)!, 100, now);
        roster.Step(game.State.SummonLease, now.AddSeconds(4), true, true);
        roster.Step(game.State.SummonLease, now.AddSeconds(4.7), true, true);
        Assert.Equal(1, HarbingerStat(game.State, "immune_to_status_ailments"));
        Inflict();
        Assert.Equal((false, 0, false, 0), game.State.ElementalAilments.Snapshot(game.State.Clock.Elapsed.TotalSeconds));
        Assert.Null((await game.Change([18859], allocate: false, refundPoints: true)).Error);
        Assert.Equal(0, HarbingerStat(game.State, "immune_to_status_ailments"));
        Inflict();
        Assert.True(game.State.ElementalAilments.Snapshot(game.State.Clock.Elapsed.TotalSeconds).Ignite);
    }

    [Fact]
    public void Harbinger_Time_and_ordinary_Chill_compose_once_in_server_attack_and_native_global_factor()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([1811, 60867]);
        EquipFamilyGem(game, FamilyGem("Burning Arrow"));
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
        game.State.Pools = Program.PoolsFor(game.State, 60);
        var skill = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Burning Arrow");
        double ordinary = ProgramValue<double>("SupportedAttackSeconds", game.State, skill.Id);
        var cold = GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Cold, 100, 100);
        Program.ApplyPlayerAilmentsLocked(game.State, cold, cold, false, false, game.State.Clock.Elapsed.TotalSeconds,
            Program.CharacterPlayerStats(game.State));
        double chill = game.State.ElementalAilments.ActionFactor(game.State.Clock.Elapsed.TotalSeconds);
        Assert.InRange(chill, .7, .95);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var now = DateTimeOffset.UtcNow;
        roster.Activate(game.State.SummonLease, Program.Harbingers.ForNode(60867)!, 100, now);
        roster.Step(game.State.SummonLease, now.AddSeconds(5), true, false);
        roster.Step(game.State.SummonLease, now.AddSeconds(5.7), true, false);
        Assert.Equal(ordinary / 1.1 / chill, ProgramValue<double>("SupportedAttackSeconds", game.State, skill.Id), 8);
        var native = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", game.State)
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal((int)Math.Round(100 * (1.1 * chill - 1)), native[Program.Stats.Key(ActionSpeed.Virtual)!.Value]);
    }

    internal static readonly ushort[][] HarbingerBuilds =
    [
        [24174, 20105, 53334, 62155, 11043, 18859, 32223, 41042],
        [1811, 60867, 23770, 3702, 58661, 34822, 63336, 39702],
        [24174, 20105, 1811, 60867, 11043, 18859, 63336, 39702],
    ];

    static GatewayCharacter HarbingerGame(ushort[]? nodes = null, bool ruthless = false) =>
        new(classRow: SkillCatalog.WitchRow, baseClass: 3, choice: "Harbinger", level: 60,
            refundPoints: 100, nodes: nodes, ruthless: ruthless);

    static int HarbingerStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static Task RefreshHarbingers(ProjectileRuntimeFixture fixture) => ProgramValue<Task>("RefreshCompanions",
        "harbinger-test", fixture.Stream, fixture.Ciphers, fixture.Conn, true);

    static Task RefreshCurses(ProjectileRuntimeFixture fixture) => ProgramValue<Task>("RefreshTemporalChains",
        "harbinger-test", fixture.Stream, fixture.Ciphers, fixture.Conn);

    [Fact]
    public async Task Harbinger_real_reflecting_variety_feeds_faster_received_curse_clock_and_Focus_suppresses_effect()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([11043, 18859, 32223, 41042, 58661, 34822, 63336, 39702]);
        await game.Command("/set_stat base_dexterity 100");
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Temporal Chains"));
        game.State.PlayerStats[Program.Stats.Key("base_cast_speed_+%")!.Value] = 9900;
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Temporal Chains");
        game.State.Monsters[2] = new("Atziri", new(32, 32, 1), 0, 1, 100)
            { Variety = Program.Monsters.Find("Metadata/Monsters/Atziri/Atziri"), Rarity = MonsterRarity.Unique };
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        await ProgramValue<Task>("CastTemporalChains", "harbinger-test", fixture.Stream, fixture.Ciphers,
            game.State, gem.Id, (32, 32), game.State.ActionCount, game.State.ProjectileEpoch);
        var curse = Assert.IsType<TemporalChainsState>(game.State.SelfTemporalChains);
        Assert.Equal(2, curse.Clock.Rate);
        Assert.Equal(-7, HarbingerStat(game.State, ActionSpeed.Virtual));
        double firstDeadline = curse.Clock.Deadline;
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var now = DateTimeOffset.UtcNow;
        roster.Activate(game.State.SummonLease, Program.Harbingers.ForNode(18859)!, 100, now);
        roster.Step(game.State.SummonLease, now.AddSeconds(4), true, true);
        roster.Step(game.State.SummonLease, now.AddSeconds(4.7), true, true);
        Assert.Equal(1, HarbingerStat(game.State, "unaffected_by_curses"));
        Assert.Equal(0, HarbingerStat(game.State, ActionSpeed.Virtual));
        Assert.Same(curse, game.State.SelfTemporalChains); // Unaffected does not delete the received curse.
        Assert.True(curse.Clock.Remaining > 0);
        Assert.Null((await game.Change([41042], false, refundPoints: true)).Error);
        Assert.Equal(1, curse.Clock.Rate);
        Assert.True(curse.Clock.Deadline > firstDeadline);
        Assert.Null((await game.Change([32223, 18859], false, refundPoints: true)).Error);
        Assert.Equal(-7, HarbingerStat(game.State, ActionSpeed.Virtual));
        Assert.True(curse.Clock.Remaining > 0);
    }

    [Fact]
    public void Harbinger_Time_changes_actual_attack_timing_without_folding_action_speed_into_native_attack_speed()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([1811, 60867]);
        EquipFamilyGem(game, FamilyGem("Burning Arrow"));
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
        game.State.Pools = Program.PoolsFor(game.State, 60);
        GrantedSkill Native() => ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State)
            .Single(s => s.EffectsPerLevelRow == Program.Skills.Find(1, "BurningArrow", 1)!.EffectsPerLevelRow);
        var before = Native();
        var beforeStats = before.Stats!.ToDictionary(p => p.Item1, p => p.Item2);
        double seconds = ProgramValue<double>("SupportedAttackSeconds", game.State, before.Id);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var now = DateTimeOffset.UtcNow;
        roster.Activate(game.State.SummonLease, Program.Harbingers.ForNode(60867)!, 100, now);
        roster.Step(game.State.SummonLease, now.AddSeconds(5), true, false);
        roster.Step(game.State.SummonLease, now.AddSeconds(5.7), true, false);
        var afterStats = Native().Stats!.ToDictionary(p => p.Item1, p => p.Item2);
        Assert.Equal(10, afterStats[Program.Stats.Key(ActionSpeed.Virtual)!.Value]);
        Assert.Equal(beforeStats[StatKeys.MainHandAttackSpeedPct], afterStats[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal(seconds / 1.1, ProgramValue<double>("SupportedAttackSeconds", game.State, before.Id), 8);
        Assert.True(afterStats[StatKeys.HundredTimesAttacksPerSecond] > beforeStats[StatKeys.HundredTimesAttacksPerSecond]);
    }

    [Fact]
    public async Task Harbinger_maintained_curse_updates_same_target_and_skill_on_effect_and_intrinsic_level_change()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([58661, 34822]);
        await game.Command("/set_stat base_dexterity 100");
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Temporal Chains"), FamilyGem("Blasphemy Support"));
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single();
        game.State.Auras[gem.Id] = new(gem.Id, gem.Effect, "TemporalChains", "Temporal Chains", 50, 69, 0, 0);
        game.State.Monsters[2] = new("target", new(32, 32, 10), 0, 1, 100)
            { Hostile = true, Variety = Program.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite") };
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        await RefreshCurses(fixture);
        var first = game.State.TemporalChains[2];
        Assert.Equal(-11, first.Slow(false)); // 15 * .75 Blasphemy, truncated once after curse effect.
        Assert.True(first.Aura);
        Assert.Equal(double.PositiveInfinity, first.Clock.Deadline);
        var expectedPacket = InstanceEntities.AddBuff(2, first.Instance, 69, Program.PlayerId, gem.Id);
        Assert.Equal(expectedPacket, ProgramValue<byte[]>("TemporalBuff", 2u, first, 0d));
        await RefreshCurses(fixture);
        Assert.Same(first, game.State.TemporalChains[2]); // No periodic remove/re-add churn.
        await game.Command("/set_stat curse_effect_+% 100");
        await RefreshCurses(fixture);
        var stronger = game.State.TemporalChains[2];
        Assert.NotEqual(first.Instance, stronger.Instance);
        Assert.Equal(-22, stronger.Slow(false));
        Assert.Equal(gem.Id, stronger.Skill);
        game.Items.ChangeSocketedGems((_, item) => Program.Items.GemName(item) == "Temporal Chains"
            ? item with { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.TypeHash)!)!.ExperienceFor(10) } : null);
        await RefreshCurses(fixture);
        var levelled = game.State.TemporalChains[2];
        Assert.NotEqual(stronger.Instance, levelled.Instance);
        Assert.Equal(-31, levelled.Slow(false));
        Assert.Equal(gem.Id, levelled.Skill);
        var host = game.Items.Items(PlayerInventories.Helmet).Single();
        game.Items.Change(PlayerInventories.Helmet, host.Id, item => item with
        {
            Sockets = [item.Sockets[0], item.Sockets[1] with { Gem = null }],
        });
        await RefreshCurses(fixture);
        Assert.Empty(game.State.TemporalChains);
        Assert.Empty(game.State.Auras);
        Assert.Equal(game.State.Monsters[2].Variety!.MovementSpeed, game.State.Monsters[2].Walk.Speed, 8);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_curse_area_changes_real_maintained_membership_and_paid_refund_restores_boundary(bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([58661, 34822], ruthless);
        await game.Command("/set_stat base_dexterity 100");
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Temporal Chains") with { Quality = 0 },
            FamilyGem("Blasphemy Support") with { Quality = 0 });
        var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single();
        game.State.Auras[gem.Id] = new(gem.Id, gem.Effect, "TemporalChains", "Temporal Chains", 50, 69, 0, 0);
        // Level-one quality-zero curse: floor(22 * floor(100 * sqrt(2)) / 100) = 31 cells.
        foreach (var (id, x) in new[] { (2u, 54), (3u, 63), (4u, 64) })
            game.State.Monsters[id] = new("boundary target", new(x, 32, 0), 0, 1, 100)
                { Hostile = true, Variety = Program.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite") };
        await using var wire = new ProjectileRuntimeFixture(game.State);
        await RefreshCurses(wire);
        Assert.Equal(new uint[] { 2, 3 }, game.State.TemporalChains.Keys.Order());
        Assert.Null((await game.Change([34822], allocate: false, refundPoints: true)).Error);
        await RefreshCurses(wire);
        Assert.Equal(new uint[] { 2 }, game.State.TemporalChains.Keys.Order());
        Assert.Equal(3500, game.State.Auras[gem.Id].ManaPercent);
    }

    [Theory]
    [InlineData(20105)] [InlineData(60867)] [InlineData(18859)]
    public async Task Harbinger_actual_granted_skill_cast_creates_an_immortal_actor_and_refund_removes_it(int node)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame();
        Assert.Null((await game.Change(PathTo(Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!,
            (ushort)node), true)).Error);
        game.State.PlayerStats[Program.Stats.Key("base_cast_speed_+%")!.Value] = 9900;
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        var skills = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).ToArray();
        var gem = Assert.Single(skills);
        var cooldown = ProgramValue<SingleUseCooldown?>("CooldownFor", game.State, gem.Effect, gem.Level, gem.Quality);
        Assert.NotNull(cooldown);
        Assert.True(Program.TryBeginSkillCooldownUse(game.State, gem.Id, gem.Effect, cooldown, false,
            () => game.State.ActionCount++, out var ticket));
        Assert.True(await ProgramValue<Task<bool>>("CastGemSkill", "harbinger-test", fixture.Stream,
            fixture.Ciphers, game.State, gem.Id, (32, 32), (32, 32), 0u, (ushort)0,
            game.State.ActionCount, ticket, false));
        var actor = Assert.Single(game.State.Monsters.Values);
        Assert.NotNull(actor.Companion);
        Assert.False(actor.Hostile);
        Assert.Null(actor.Attack);
        Assert.Empty(SavedMonster.Save([actor], 0));
        Assert.Null(ProgramValue<object?>("ApplyMonsterHitLocked", game.State, actor, 1000));
        Assert.Equal(1u, actor.Life);
        Assert.Null((await game.Change([(ushort)node], false, refundPoints: true)).Error);
        Assert.Empty(game.State.Monsters);
        Assert.Empty(game.State.CompanionBuffs);
    }

    [Theory]
    [InlineData("refund")] [InlineData("death")] [InlineData("area")] [InlineData("replacement")]
    public async Task Harbinger_queued_creation_rechecks_source_owner_area_and_replacement_at_cipher_gate(string change)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([24174, 20105]);
        await using var fixture = new ProjectileRuntimeFixture(game.State);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var source = Program.Harbingers.ForNode(20105)!;
        roster.Activate(game.State.SummonLease, source, 100, DateTimeOffset.UtcNow);
        await fixture.HoldOutput();
        var pending = RefreshHarbingers(fixture);
        await Task.Delay(20);
        Task? refund = null;
        if (change == "refund")
        {
            refund = game.Change([20105], false, refundPoints: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (game.State.MainAscendancy.Allocated.Contains((ushort)20105))
                await Task.Delay(5, timeout.Token);
        }
        else if (change == "death") { game.State.PlayerLife = 0; roster.Clear(game.State.SummonLease); }
        else if (change == "area") game.State.LeavingArea = true;
        else roster.Activate(game.State.SummonLease, source, 100, DateTimeOffset.UtcNow);
        fixture.ReleaseOutput();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        if (refund is not null) await refund;
        Assert.Empty(game.State.Monsters);
        Assert.Empty(game.State.CompanionBuffs);
        if (change == "replacement")
        {
            await RefreshHarbingers(fixture);
            Assert.Single(game.State.Monsters);
        }
    }

    [Fact]
    public async Task Harbinger_Ruthless_death_clears_summons_and_recovers_boss_before_queued_publication()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([24174, 20105], ruthless: true);
        var conn = new ClientState(new(0, "1_1_2", 128, 128, 32, 32, null, []), 0, 1)
            { Character = game.Character, ClassRow = SkillCatalog.WitchRow, Ruthless = true };
        ProgramValue<object?>("LoadProgress", conn, 1);
        conn.PlayerLife = conn.PlayerMaxLife;
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", conn);
        var source = Program.Harbingers.ForNode(20105)!;
        Assert.True(roster.Activate(conn.SummonLease, source, 100, DateTimeOffset.UtcNow));
        var boss = new SpawnedMonster("mode boss", new(32, 32, 0), 0, 2, 100)
            { Variety = Program.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")!, Life = 25 };
        conn.Monsters[42] = boss;
        await using var wire = new ProjectileRuntimeFixture(conn);
        await RefreshHarbingers(wire);
        Assert.Contains(conn.Monsters.Values, m => m.Companion is not null);
        await wire.HoldOutput();
        conn.PlayerLife = 0;
        var death = ProgramValue<Task>("PlayerDied", "harbinger-mode", wire.Stream, wire.Ciphers, conn, "death");
        Assert.False(death.IsCompleted);
        Assert.Empty(roster.Read(conn.SummonLease, [20105], DateTimeOffset.UtcNow));
        Assert.Equal(RuthlessGameplay.BossLifeAfterDeath(25, 100,
            Program.RuthlessSettings.Current.CampaignBossLifeRecoveryFraction), boss.Life);
        wire.ReleaseOutput();
        await death.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(conn.Monsters.Values, m => m.Companion is not null);
        Assert.Empty(conn.CompanionBuffs);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task Harbinger_legal_build_full_paid_refund_and_reload_restore_all_consumers(int index, bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame(ruthless: ruthless);
        var nodes = HarbingerBuilds[index];
        var before = game.State.Pools;
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Equal(index == 0 ? 50 : 0, HarbingerStat(game.State, "mana_recovery_rate_+%"));
        Assert.Equal(index == 0 ? -30 : 0, HarbingerStat(game.State, "buff_time_passed_+%_only_buff_category"));
        Assert.Equal(index == 1 ? 55 : 0, HarbingerStat(game.State, "virtual_cooldown_speed_+%"));
        Assert.Equal(index == 0 ? 0u : (uint)((game.State.Pools.Life * .10m + game.State.Pools.Mana * .20m)
            * (100 + game.State.Pools.EnergyShieldPct) / 100), game.State.Pools.EnergyShield);
        Assert.Equal(game.State.Pools.EnergyShield, game.Reload().Pools.EnergyShield);
        var grants = ProgramValue<HarbingerCompanionCatalog.Companion[]>("HarbingerGrants", game.Character);
        Assert.Equal(index == 2 ? 3 : index == 0 ? 2 : 1, grants.Length);
        var skills = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State);
        Assert.All(grants, grant => Assert.Contains(skills, skill =>
            skill.EffectsPerLevelRow == Program.Skills.FindByRow(1, grant.SummonEffect, grant.SummonLevel)!.EffectsPerLevelRow));
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        Assert.Empty(ProgramValue<HarbingerCompanionCatalog.Companion[]>("HarbingerGrants", game.Character));
        Assert.Equal(before.Life, game.State.Pools.Life);
        Assert.Equal(before.Mana, game.State.Pools.Mana);
        Assert.Equal(before.EnergyShield, game.State.Pools.EnergyShield);
        Assert.Equal(0, HarbingerStat(game.State, "mana_recovery_rate_+%"));
        Assert.Equal(0, HarbingerStat(game.State, "base_non_chaos_damage_bypass_energy_shield_%"));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(game.State.Pools.EnergyShield, game.Reload().Pools.EnergyShield);
    }

    [Fact]
    public async Task Harbinger_small_nodes_and_recovery_notable_change_real_regen_and_ES_recharge_once()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame();
        uint mana = game.State.Pools.Mana;
        Assert.Null((await game.Change([24174, 20105, 53334, 62155], true)).Error);
        var stats = Program.CharacterPlayerStats(game.State);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(mana * 1.05 * 1.30 * 1.50, PanelStats.ManaPerMinute(Get, mana), 7);
        Assert.Equal(100 * 20 * 1.20 * 1.50, EnergyShield.RechargePerMinute(100, Get), 7);
        var es = new EnergyShield(); es.SetMaximum(100); es.Damage(1, false, 0);
        es.Advance(2, 2.1, Get, true);
        Assert.Equal(6u, es.Current); // 60/s for 0.1 s; recovery rate does not change the delay.
        Assert.Null((await game.Change([62155], false, refundPoints: true)).Error);
        Assert.Equal(mana * 1.05 * 1.30, ProgramValue<double>("ManaRegenPerSecond", game.State) * 60, 7);
    }

    [Fact]
    public async Task Harbinger_extra_ES_uses_final_pools_and_bypass_preserves_Ward_and_chaos()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([63336, 39702]);
        var pools = game.State.Pools;
        Assert.Equal(HarbingerDefences.Maximum(0, pools.Life, pools.Mana, pools.EnergyShieldPct, 10, 20), pools.EnergyShield);
        game.State.EnergyShield.SetMaximum(100, true);
        game.State.Ward.SetMaximum(10);
        int lifeDamage = Program.DamagePlayerHit(game.State, 110, 20, 0, Program.CharacterPlayerStats(game.State));
        Assert.Equal(50, lifeDamage); // Ward takes 10 non-chaos; 30 bypass ES and all 20 chaos bypass ES.
        Assert.Equal(30u, game.State.EnergyShield.Current);
        Assert.True(game.State.Ward.Broken);
        Assert.Null((await game.Change([39702], false, refundPoints: true)).Error);
        Assert.Equal(0u, game.State.Pools.EnergyShield);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_Temporal_Chains_reservation_requires_a_gem_and_Blasphemy_and_refund_restores_cost(bool ruthless)
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([58661, 34822], ruthless);
        Assert.Empty(ProgramValue<HarbingerCompanionCatalog.Companion[]>("HarbingerGrants", game.Character));
        await game.Command("/set_stat base_dexterity 100");
        await game.Command("/set_stat base_intelligence 100");
        EquipFamilyGem(game, FamilyGem("Temporal Chains"), FamilyGem("Blasphemy Support"));
        var skill = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State)
            .Single(s => s.EffectsPerLevelRow == Program.Skills.Find(1, "TemporalChains", 1)!.EffectsPerLevelRow);
        Assert.Equal((0, 0), ProgramValue<(int Flat, int Percent)?>("SupportedReservation", game.State, skill.Id));
        game.State.Auras[skill.Id] = new(skill.Id, Program.Skills.EffectRow("TemporalChains"), "TemporalChains",
            "Temporal Chains", 50, 69, 0, 0, SourceBuff: 51);
        Assert.Null((await game.Change([34822], false, refundPoints: true)).Error);
        Assert.Equal((0, 3500), ProgramValue<(int Flat, int Percent)?>("SupportedReservation", game.State, skill.Id));
        Assert.Equal(3500, game.State.Auras[skill.Id].ManaPercent);
        Assert.True(ProgramValue<int>("ReservedMana", game.State) > 0);
        Assert.Equal(skill.Id, game.State.Auras[skill.Id].SkillId);
    }

    [Fact]
    public async Task Harbinger_failed_source_refund_preserves_companion_and_durable_source()
    {
        if (!DataAvailable()) return;
        using var game = HarbingerGame([24174, 20105]);
        var roster = ProgramValue<HarbingerRoster>("CompanionsOf", game.State);
        var source = Program.Harbingers.ForNode(20105)!;
        Assert.True(roster.Activate(game.State.SummonLease, source, 100, DateTimeOffset.UtcNow));
        string before = game.SavedJson;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAnyAsync<IOException>(() => game.Change([20105], false, refundPoints: true)); }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, game.SavedJson);
        Assert.Single(roster.Read(game.State.SummonLease, [20105], DateTimeOffset.UtcNow));
        Assert.Contains((ushort)20105, game.State.MainAscendancy.Allocated);
    }
}
