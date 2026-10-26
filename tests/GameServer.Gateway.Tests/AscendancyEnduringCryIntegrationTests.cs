using System.Buffers.Binary;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static GatewayCharacter CryCharacter(int quality = 0, string choice = "Ancestral Commander")
    {
        Assert.True(DataAvailable());
        var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: choice, refundPoints: 60, level: 30);
        var gem = Program.Items.FindGem("Enduring Cry")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new(SocketColour.Red, ItemCatalog.Create(gem.Type) with
                { GemVariant = gem.Variant.Index, Quality = (byte)quality })], Links = [0],
        });
        CryInput(game, "virtual_strength_for_requirements", 1000);
        return game;
    }

    static void CryInput(GatewayCharacter game, string name, int value) =>
        game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static SpawnedMonster CryMonster(GatewayCharacter game, uint id, MonsterRarity rarity, int x = 40, int y = 32,
        bool hostile = true, bool owned = false)
    {
        var monster = new SpawnedMonster("warcry target", new WalkEstimator(x, y, 0), 0, 0, 1000)
        {
            Rarity = rarity, Hostile = hostile,
            Summon = owned ? new SummonRoster.Member(1, DateTimeOffset.UtcNow.AddMinutes(1)) : null,
        };
        game.State.Monsters[id] = monster;
        return monster;
    }

    static async Task CryUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    static void RemoveCrySource(GatewayCharacter game)
    {
        var host = game.Items.Items(PlayerInventories.Helmet).Single();
        Assert.NotNull(game.Items.Remove(PlayerInventories.Helmet, host.Id));
    }

    static int CryStat(GatewayCharacter game, string name) =>
        Program.EnduringCryStats(game.State).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static int CryBuffOffset(byte[] bytes)
    {
        for (int i = 0; i + 27 <= bytes.Length; i++)
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i)) == InstanceEntities.AddBuffId
                && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 2)) == Program.PlayerId
                && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 14)) == EnduringCry.BuffDefinition)
                return i;
        return -1;
    }

    [Theory]
    [InlineData(100, 0, 960)]
    [InlineData(100, 50, 1200)]
    [InlineData(-100, 0, 0)]
    public async Task Paid_Enduring_Cry_applies_owner_buff_effect_once(int self, int warcry, int regen)
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        CryInput(game, "buff_effect_on_self_+%", self);
        CryInput(game, "warcry_buff_effect_+%", warcry);
        CryMonster(game, 0x4000, MonsterRarity.Unique);
        uint mana = game.State.PlayerMana;

        var result = await game.Cast("Enduring Cry");

        Assert.True(result.Accepted);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(3, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(regen, CryStat(game, "life_regeneration_rate_per_minute_%"));
        Assert.Equal(regen, game.PlayerStat("life_regeneration_rate_per_minute_%"));
        Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
        Assert.True(CryBuffOffset(result.Bytes) >= 0);
        Assert.Equal(1000u, game.State.Monsters[0x4000].Life);
    }

    [Theory]
    [InlineData(MonsterRarity.Normal, 4, 0, 0)]
    [InlineData(MonsterRarity.Normal, 5, 1, 120)]
    [InlineData(MonsterRarity.Magic, 3, 1, 120)]
    [InlineData(MonsterRarity.Rare, 1, 2, 240)]
    [InlineData(MonsterRarity.Unique, 1, 3, 480)]
    public async Task Actual_Enduring_Cry_pays_once_and_grants_current_power_without_a_damage_hit(
        MonsterRarity rarity, int enemies, int charges, int regen)
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        for (uint i = 0; i < enemies; i++) CryMonster(game, 0x4000 + i, rarity);
        uint mana = game.State.PlayerMana;
        var result = await game.Cast("Enduring Cry");
        Assert.True(result.Accepted);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(charges, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(regen, CryStat(game, "life_regeneration_rate_per_minute_%"));
        Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
        Assert.All(game.State.Monsters.Values, m => Assert.Equal(1000u, m.Life));
        Assert.Equal(double.NegativeInfinity, game.State.LastEnemyHitAt);
        int at = CryBuffOffset(result.Bytes);
        Assert.True(at >= 0, "The actual cast must publish visible Enduring Cry buff row 1541.");
        Assert.Equal(2, result.Bytes[at + 22]); // Timed buff, no invented recovery-values payload.
        float seconds = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(result.Bytes.AsSpan(at + 23)));
        Assert.InRange(seconds, .1f, 2.5f);
        Assert.False((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(1, Assert.Single(game.State.SingleUseCooldowns).Value.UseVersion);
    }

    [Fact]
    public async Task Accepted_Enduring_Cry_uses_completion_positions_life_and_hostility_after_later_actions()
    {
        using var game = CryCharacter();
        var entering = CryMonster(game, 0x4000, MonsterRarity.Rare, 1000);
        var dying = CryMonster(game, 0x4001, MonsterRarity.Unique);
        CryMonster(game, 0x4002, MonsterRarity.Unique, hostile: false);
        CryMonster(game, 0x4003, MonsterRarity.Unique, owned: true);
        uint mana = game.State.PlayerMana;
        var result = await game.CryChangeAfterExecution(() =>
        {
            lock (game.State.Monsters)
            {
                double now = game.State.Clock.Elapsed.TotalSeconds;
                game.State.Walk.Teleport(64, 32, now);
                entering.Walk.Teleport(120, 32, now); // 56 from current player, 88 from original player.
                dying.Life = 0;
                game.State.ActionCount++;
            }
        });
        Assert.Null(result.Error);
        Assert.Equal(2, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(240, CryStat(game, "life_regeneration_rate_per_minute_%"));
        Assert.Equal(mana - 13, game.State.PlayerMana);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("source")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    public async Task Accepted_Enduring_Cry_aborts_invalid_owner_or_source_without_refunding_cost_or_cooldown(string change)
    {
        using var game = CryCharacter();
        CryMonster(game, 0x4000, MonsterRarity.Unique);
        uint mana = game.State.PlayerMana;
        var casting = game.Cast("Enduring Cry");
        await CryUntil(() => game.State.PlayerMana == mana - 13);
        lock (game.State.Monsters)
        {
            if (change == "death") { game.State.PlayerLife = 0; Program.EnduringCryDied(game.State); }
            if (change == "source") RemoveCrySource(game);
            if (change == "transfer") Assert.True(game.State.TryBeginAreaChange(new(2, 0, false), out _));
            if (change == "disconnect") game.State.Closed = true;
        }
        await casting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(1, Assert.Single(game.State.SingleUseCooldowns).Value.UseVersion);
        Assert.Empty(Program.EnduringCryStats(game.State));
        Assert.Equal(0, game.PlayerStat("current_endurance_charges"));
    }

    [Fact]
    public async Task Pause_freezes_pending_completion_and_spent_cooldown_then_resumes_once()
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 300);
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        uint mana = game.State.PlayerMana;
        var casting = game.Cast("Enduring Cry");
        await CryUntil(() => game.State.PlayerMana == mana - 13);
        game.State.Clock.Stop();
        double paused = game.State.Clock.Elapsed.TotalSeconds;
        var cooldown = Assert.Single(game.State.SingleUseCooldowns).Value;
        double remaining = cooldown.RemainingBaseMilliseconds;
        await Task.Delay(300);
        Assert.False(casting.IsCompleted);
        Assert.Empty(Program.EnduringCryStats(game.State));
        Assert.Equal(paused, game.State.Clock.Elapsed.TotalSeconds);
        Assert.Equal(remaining, cooldown.RemainingBaseMilliseconds);
        game.State.Clock.Start();
        Assert.True((await casting.WaitAsync(TimeSpan.FromSeconds(3))).Accepted);
        Assert.Equal(2, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(mana - 13, game.State.PlayerMana);
    }

    [Fact]
    public async Task Buff_regeneration_integrates_exact_lifetime_and_expires_before_ten_second_charges()
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        CryInput(game, "skill_effect_duration_+%", -96); // Current level-one buff: 2500 * .04 = 100 ms.
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        Assert.True((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
        double expected = game.State.Pools.Life * .04 * .1;
        await Task.Delay(180);
        Assert.Empty(Program.EnduringCryStats(game.State));
        Assert.Equal(expected, Program.EnduringCryRecovery(game.State, 0, game.State.Clock.Elapsed.TotalSeconds), 6);
        Assert.Equal(0, Program.EnduringCryRecovery(game.State, 0, game.State.Clock.Elapsed.TotalSeconds));
        Assert.Equal(2, game.PlayerStat("current_endurance_charges"));
        Assert.NotEmpty(Program.EnduringCryBuffPublication(game.State, game.State.ProjectileEpoch)!);
    }

    [Theory]
    [InlineData("mana")]
    [InlineData("life")]
    public async Task Unpayable_Enduring_Cry_does_not_start_an_action_or_spend_a_cooldown(string pool)
    {
        using var game = CryCharacter();
        if (pool == "mana") game.State.PlayerMana = 12;
        else { CryInput(game, "base_skill_cost_life_instead_of_mana", 1); game.State.PlayerLife = 13; }
        uint mana = game.State.PlayerMana, life = game.State.PlayerLife;
        int action = game.State.ActionCount;
        Assert.False((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(life, game.State.PlayerLife);
        Assert.Equal(action, game.State.ActionCount);
        Assert.Empty(game.State.SkillReadyAt);
        Assert.All(game.State.SingleUseCooldowns.Values, c => Assert.Equal(0, c.UseVersion));
    }

    [Theory]
    [InlineData("skill_cooldown_group_override", 36)]
    [InlineData("virtual_skill_ignores_cooldown", 1)]
    [InlineData("warcry_skills_share_cooldowns", 1)]
    [InlineData("warcry_cooldown_modifier_ms", 100)]
    [InlineData("warcry_skills_cooldown_is_4_seconds", 1)]
    [InlineData("non_instant_warcries_have_no_cooldown", 1)]
    [InlineData("base_spell_cast_time_ms_override", 100)]
    [InlineData("warcry_speed_+%_if_not_warcried_recently", 100)]
    public async Task Unsupported_Enduring_Cry_input_rejects_before_echo_cost_and_cooldown(string stat, int value)
    {
        using var game = CryCharacter();
        CryInput(game, stat, value);
        await game.PrimeTimingPools();
        var skill = game.GemEffects().Single();
        uint mana = game.State.PlayerMana;
        int action = game.State.ActionCount;
        var result = await game.TimingInput("ground", skill.Id, 0x4000);
        Assert.Equal(InstanceProtocol.LifeUpdate(Program.PlayerId, mana, 0, pool: 1), result.Bytes);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(action, game.State.ActionCount);
        Assert.Empty(game.State.SkillReadyAt);
        Assert.Empty(game.State.SingleUseCooldowns);
    }

    [Theory]
    [InlineData("Enduring Cry")]
    [InlineData("Rallying Cry")]
    [InlineData("General's Cry")]
    public async Task Actual_native_same_group_peer_and_duplicate_effect_ids_are_not_admitted(string peer)
    {
        using var game = CryCharacter();
        var gem = game.GemEffects().Single();
        EquipBlueGems(game, peer);
        var grants = game.GemEffects();
        string peerEffect = peer switch { "Rallying Cry" => "RallyingCry", "General's Cry" => "GeneralsCry", _ => "EnduringCry" };
        Assert.True(grants.Count >= 2);
        Assert.Contains(grants, g => Program.Skills.EffectId(g.Effect) == peerEffect && g.Id != gem.Id);
        if (peer == "Enduring Cry") Assert.Equal(2, grants.Count(g => g.Effect == gem.Effect));
        Assert.False(Program.EnduringCrySingletonGroup(game.State, gem.Id));
        uint mana = game.State.PlayerMana;
        int effects = 0;
        var plan = ProgramValue<SingleUseCooldown?>("CooldownFor", game.State, gem.Effect, gem.Level, gem.Quality);
        Assert.Null(plan);
        Assert.False(Program.TryBeginCooldownUse(game.State, gem.Effect, plan, false, () => effects++, out _, skillId: gem.Id));
        Assert.Equal(0, effects);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Empty(game.State.SkillReadyAt);
    }

    [Fact]
    public async Task Call_of_the_Ancestors_real_allocation_and_paid_refund_compose_quality_recovery_once()
    {
        using var game = CryCharacter(quality: 20);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 48740), true)).Error);
        var skill = game.GemEffects().Single();
        var own = Program.SupportedSkill(game.State, skill.Id);
        var player = Program.CharacterPlayerStats(game.State);
        var profile = EnduringCry.For(Program.Skills, Program.Stats, skill.Effect, own.Level, own.Quality, player, own.Stats)!;
        Assert.Equal(267, profile.Timing.DurationMilliseconds);
        Assert.Equal(10, profile.RecoveryPercent);
        Assert.Equal(8000 / 1.1 / 1000, profile.CooldownSeconds, 8);
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        uint mana = game.State.PlayerMana;
        Assert.True((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(1.1, Assert.Single(game.State.SingleUseCooldowns).Value.Rate, 8);
        ushort refunds = game.State.RefundPoints;
        Assert.Null((await game.Change([48740], false, refundPoints: true)).Error);
        Assert.True(game.State.RefundPoints < refunds);
        Assert.Equal(1.4, Assert.Single(game.State.SingleUseCooldowns).Value.Rate, 8);
        Assert.Equal(0, game.PlayerStat("warcry_speed_+%"));
        Assert.False((await game.Cast("Enduring Cry")).Accepted); // Refund retains the already-spent budget.
        Assert.Equal(mana - 13, game.State.PlayerMana);
    }

    [Fact]
    public async Task Ancestral_Endurance_real_maximum_allows_four_charges_from_one_unique()
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 23061), true)).Error);
        CryMonster(game, 0x4000, MonsterRarity.Unique);
        Assert.True((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(5, game.PlayerStat("virtual_maximum_endurance_charges"));
        Assert.Equal(4, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(480, CryStat(game, "life_regeneration_rate_per_minute_%"));
        Assert.Null((await game.Change([23061], false, refundPoints: true)).Error);
        Assert.Equal(3, game.PlayerStat("current_endurance_charges"));
    }

    [Fact]
    public async Task Rampager_disables_actual_Enduring_Cry_before_any_paid_use()
    {
        using var game = CryCharacter(choice: "Behemoth");
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 49937), true)).Error);
        uint mana = game.State.PlayerMana;
        Assert.False((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Empty(game.State.SkillReadyAt);
        Assert.Empty(game.State.SingleUseCooldowns);
        Assert.Null((await game.Change([49937], false, refundPoints: true)).Error);
        CryInput(game, "warcry_speed_+%", 900);
        Assert.True((await game.Cast("Enduring Cry")).Accepted);
        Assert.Equal(mana - 13, game.State.PlayerMana);
    }

    [Fact]
    public async Task Enduring_Cry_actual_resource_writer_restores_life_only_during_the_independent_buff_lifetime()
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        CryInput(game, "skill_effect_duration_+%", -80); // 500 ms, independent of the ten-second charge timer.
        CryMonster(game, 0x4000, MonsterRarity.Unique);
        game.State.PlayerLife = 10;
        Assert.True((await game.Cast("Enduring Cry")).Accepted);
        uint maximum = game.State.Pools.Life;
        double expected = maximum * .08 * .5;
        var result = await game.CryResourceRecovery();
        Assert.Null(result.Error);
        Assert.InRange((double)game.State.PlayerLife - 10, Math.Floor(expected) - 2, Math.Floor(expected));
        Assert.Empty(Program.EnduringCryStats(game.State));
        Assert.Equal(3, game.PlayerStat("current_endurance_charges"));
    }

    [Fact]
    public async Task Enduring_Cry_actual_instant_object_route_preserves_an_existing_walk_and_action()
    {
        using var game = CryCharacter();
        CryInput(game, "warcries_are_instant", 1);
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.Walk.Order(120, 32, now);
        game.State.ActionCount = 27;
        game.State.ChannelSkill = 0x222;
        uint mana = game.State.PlayerMana;
        var skill = game.GemEffects().Single();
        var result = await game.TimingInput("instant", skill.Id, Program.PlayerId);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(27, game.State.ActionCount);
        Assert.Equal((ushort)0x222, game.State.ChannelSkill);
        Assert.Equal((120, 32), game.State.Walk.Destination);
        Assert.Equal(2, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
        Assert.True(result.Bytes.AsSpan().IndexOf(new byte[] { 0x01, 0xce }) >= 0);
        Assert.True(CryBuffOffset(result.Bytes) >= 0);
    }

    [Fact]
    public async Task Enduring_Cry_framed_self_target_accepts_pause_resume_and_a_later_walk_before_completion()
    {
        using var game = CryCharacter();
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        uint mana = game.State.PlayerMana;
        var result = await game.CrySelfTargetWithPause();
        Assert.Null(result.Error);
        Assert.Equal(mana - 13, game.State.PlayerMana);
        Assert.Equal(2, game.PlayerStat("current_endurance_charges"));
        Assert.True(result.Bytes.AsSpan().IndexOf(new byte[] { 0x01, 0xab }) >= 0);
        Assert.True(result.Bytes.AsSpan().IndexOf(InstancePause.State(true)) >= 0);
        Assert.True(result.Bytes.AsSpan().IndexOf(InstancePause.State(false)) >= 0);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("death")]
    [InlineData("source")]
    [InlineData("transfer")]
    [InlineData("pause")]
    public async Task Enduring_Cry_queued_buff_publication_revalidates_at_the_actual_cipher_gate(string change)
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        if (change == "expiry") CryInput(game, "skill_effect_duration_+%", -96);
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        var result = await game.CryWithQueuedPublication(async () =>
        {
            Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
            if (change == "expiry") await Task.Delay(180);
            if (change == "death") game.State.PlayerLife = 0;
            if (change == "source") RemoveCrySource(game);
            if (change == "transfer") Assert.True(game.State.TryBeginAreaChange(new(2, 0, false), out _));
            if (change == "pause") game.State.Clock.Stop();
        });
        Assert.Equal(-1, CryBuffOffset(result.Bytes));
        if (change == "pause")
        {
            Assert.Equal(1, CryStat(game, "affected_by_a_warcry"));
            game.State.Clock.Start();
            var retry = await game.CryRefresh();
            Assert.True(CryBuffOffset(retry.Bytes) >= 0);
        }
        else Assert.Empty(Program.EnduringCryStats(game.State));
    }

    [Fact]
    public async Task Enduring_Cry_life_publication_reads_current_life_after_the_cipher_queue()
    {
        using var game = CryCharacter();
        CryInput(game, "warcry_speed_+%", 900);
        CryMonster(game, 0x4000, MonsterRarity.Rare);
        var result = await game.CryWithQueuedPublication(() =>
        {
            game.State.PlayerLife = 17;
            return Task.CompletedTask;
        });
        Assert.Equal(17u, game.State.PlayerLife);
        Assert.True(result.Bytes.AsSpan().EndsWith(InstanceProtocol.LifeUpdate(Program.PlayerId, 17)));
        Assert.True(CryBuffOffset(result.Bytes) >= 0);
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> CryChangeAfterExecution(Action mutation) => Exchange(async (stream, cipher) =>
        {
            var skill = GemEffects().Single();
            var plan = ProgramValue<SingleUseCooldown?>("CooldownFor", State, skill.Effect, skill.Level, skill.Quality);
            Assert.True(Program.TryBeginCooldownUse(State, skill.Effect, plan, false, () => { }, out var ticket,
                skillId: skill.Id));
            var cast = ProgramValue<Task<bool>>("CastGemSkill", "executing-enduring-cry", stream, cipher, State,
                skill.Id, (32, 32), (48, 32), 0u, (ushort)0, null, ticket, false);
            Assert.False(cast.IsCompleted);
            mutation();
            Assert.True(await cast);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CryWithQueuedPublication(Func<Task> mutation) => Exchange(async (stream, cipher) =>
        {
            var skill = GemEffects().Single();
            var plan = ProgramValue<SingleUseCooldown?>("CooldownFor", State, skill.Effect, skill.Level, skill.Quality);
            Assert.True(Program.TryBeginCooldownUse(State, skill.Effect, plan, false, () => { }, out var ticket,
                skillId: skill.Id));
            var gate = Program.SendGates.GetValue(cipher, _ => new(1, 1));
            await gate.WaitAsync();
            var cast = ProgramValue<Task<bool>>("CastGemSkill", "queued-enduring-cry", stream, cipher, State,
                skill.Id, (32, 32), (48, 32), 0u, (ushort)0, null, ticket, false);
            try
            {
                await CryUntil(() => Program.EnduringCryStats(State).Count > 0);
                await mutation();
            }
            finally { gate.Release(); }
            Assert.True(await cast);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CryRefresh() => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("RefreshEnduringCry", "enduring-cry-refresh", stream, cipher, State);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CryResourceRecovery() => Exchange(async (stream, cipher) =>
        {
            var recovery = ProgramValue<Task>("RunResourceRecovery", "enduring-cry-recovery", stream, cipher, State);
            try
            {
                await Task.Delay(650);
                uint after = State.PlayerLife;
                await Task.Delay(150);
                Assert.Equal(after, State.PlayerLife);
            }
            finally { State.Closed = true; }
            await recovery;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CrySelfTargetWithPause() => Exchange(async (stream, cipher) =>
        {
            var skill = GemEffects().Single();
            byte[] self = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(Program.PlayerId)
                .U16(skill.Id).U16(0).U16(0x401).ToArray();
            await Program.OnClientMessages("enduring-cry-self", stream, cipher,
                [(InstanceTransfer.UseSkillOnObjectId, self)], [], State).WaitAsync(TimeSpan.FromSeconds(1));
            await CryUntil(() => State.PlayerMana < State.Pools.Mana);
            await Program.OnClientMessages("enduring-cry-pause", stream, cipher,
                [(InstancePause.RequestId, new byte[] { 0x01, 0xd4, 1 })], [], State).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(State.Paused);
            await Task.Delay(100);
            Assert.Empty(Program.EnduringCryStats(State));
            await Program.OnClientMessages("enduring-cry-resume", stream, cipher,
                [(InstancePause.RequestId, new byte[] { 0x01, 0xd4, 0 })], [], State).WaitAsync(TimeSpan.FromSeconds(1));
            ushort move = Program.Skills.ActionSkillId("Move")!.Value;
            byte[] walk = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32(48).U32(32)
                .U16(move).U16(0).U16(0x401).ToArray();
            await Program.OnClientMessages("enduring-cry-later-walk", stream, cipher,
                [(InstanceProtocol.UseSkillAtLocationId, walk)], [], State);
            await CryUntil(() => Program.EnduringCryStats(State).Count > 0);
            // The semantic checks above intentionally leave the shared reader free throughout completion.
            // Let its guarded completion publisher finish before closing the encrypted test wire.
            await Task.Delay(50);
            return null;
        });
    }
}
