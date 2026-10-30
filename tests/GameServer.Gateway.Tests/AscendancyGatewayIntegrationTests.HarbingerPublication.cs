using System.Diagnostics;
using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_lethal_Ignite_persists_Hardcore_death_before_held_roster_or_curse_publication(bool curse)
    {
        using var game = new GatewayCharacter(classRow: SkillCatalog.WitchRow, baseClass: 3,
            choice: "Harbinger", level: 60, hardcore: true);
        var at = DateTimeOffset.UtcNow;
        var character = new Character(Guid.NewGuid(), Guid.NewGuid(), game.Character, CharacterClass.Witch,
            60, "Hardcore", "1_1_2", at, at);
        var store = new SqliteAccountStore(Saves.Db);
        store.AddCharacter(character);
        var conn = new ClientState(new(0, "1_1_2", 128, 128, 32, 32, null, []), 0, 1)
        {
            Character = game.Character, CharacterId = character.Id, ClassRow = SkillCatalog.WitchRow,
            League = character.League, Hardcore = true,
        };
        ProgramValue<object?>("LoadProgress", conn, 1);
        conn.PlayerLife = 1;
        conn.EnergyShield.Empty();
        _ = ProgramValue<HarbingerRoster>("CompanionsOf", conn); // Even an empty attached roster used to await output.
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (curse) conn.SelfTemporalChains = new(80, 1, new EffectClock(20, now), -15, -9, -25, false);
        var fire = DamagePacket.Of(DamageType.Fire, 100_000, 100_000);
        Program.ApplyPlayerAilmentsLocked(conn, fire, fire, true, false, now, Program.CharacterPlayerStats(conn));
        Assert.True(conn.ElementalAilments.HasEffects);
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();
        var worker = ProgramValue<Task>("RunResourceRecovery", "harbinger-lethal", wire.Stream, wire.Ciphers, conn);
        try
        {
            var waiting = Stopwatch.StartNew();
            while (waiting.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (conn.Monsters) if (conn.HardcoreDeathRecorded) break;
                await Task.Delay(10);
            }
            lock (conn.Monsters)
            {
                Assert.Equal(0u, conn.PlayerLife);
                Assert.True(conn.HardcoreDeathRecorded);
            }
            Assert.False(worker.IsCompleted);
            Assert.Equal(0, wire.Gate.CurrentCount);
            Assert.Equal(0, wire.Available);
            var saved = store.FindCharacter(game.Character)!;
            Assert.Equal("Standard", saved.League);
            Assert.Equal("Hardcore", saved.HardcoreDeath!.SourceLeague);
            Assert.False(saved.HardcoreDeath.PermanentlyDead);
            conn.Closed = true; // A disconnect before output resumes cannot skip the committed terminal outcome.
            Assert.NotNull(store.FindCharacter(game.Character)!.HardcoreDeath);
        }
        finally
        {
            conn.Closed = true;
            wire.ReleaseOutput();
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    static UtilityFlasks.Active HarbingerSilver(GatewayCharacter game, double? seconds = null)
    {
        var type = Program.Items.Find("Silver Flask")!;
        var flask = game.Items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Charges = 60 });
        var utility = Program.Items.UtilityFlaskOf(type)!;
        var (stats, duration) = UtilityFlasks.For(utility, flask.Item, new Dictionary<string, int>(), _ => 0);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        var effect = new UtilityFlasks.Active(flask.Id, 0, type.Name, utility,
            game.State.NextBuff++, game.State.NextBuff++, stats, now, now + (seconds ?? duration));
        UtilityFlasks.Start(game.State, effect, () => { });
        return effect;
    }

    static Task<bool> HarbingerSilverStart(ProjectileRuntimeFixture wire, UtilityFlasks.Active effect) =>
        ProgramValue<Task<bool>>("SendUtilityFlaskStart", "harbinger-flask", wire.Stream, wire.Ciphers, wire.Conn, effect);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Harbinger_Silver_start_keeps_both_adds_before_queued_expiry_or_death_removals(bool death)
    {
        using var game = HarbingerGame();
        var effect = HarbingerSilver(game);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        await wire.HoldOutput();
        var start = HarbingerSilverStart(wire, effect);
        // Queue an end immediately after the first accepted start transaction. With separate sends this end
        // can remove both buffs before the second add resumes, leaving maintained Onslaught behind forever.
        var end = Program.Send("harbinger-flask-end", wire.Stream, wire.Ciphers, [], null, atGate: () =>
        {
            lock (game.State.Monsters)
            {
                if (death) game.State.PlayerLife = 0;
                var ended = UtilityFlasks.EndDue(game.State, effect.Ends + 1, death, null, () => { }, out _);
                Assert.Same(effect, Assert.Single(ended));
                return [.. UtilityFlasks.EndPackets(effect, Program.PlayerId).SelectMany(p => p)];
            }
        });
        Assert.False(start.IsCompleted);
        Assert.False(end.IsCompleted);
        wire.ReleaseOutput();
        Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(5)));
        await end.WaitAsync(TimeSpan.FromSeconds(5));
        var bytes = await wire.ReadWire();
        var (_, prefix) = HarbingerStatPrefix(bytes);
        var extra = InstanceEntities.AddBuff(Program.PlayerId, effect.ExtraBuff!.Value,
            effect.Flask.Extra!.BuffRow, Program.PlayerId);
        var removals = UtilityFlasks.EndPackets(effect, Program.PlayerId).SelectMany(p => p).ToArray();
        int sourceLength = Flasks.Buff(Program.PlayerId, effect.Buff, effect.Flask.BuffRow, effect.Slot, 1).Length;
        Assert.Equal(extra, bytes.AsSpan(prefix + sourceLength, extra.Length).ToArray());
        Assert.Equal(removals, bytes.AsSpan(prefix + sourceLength + extra.Length).ToArray());
        Assert.False(game.State.UtilityFlaskEffects.Any);
        Assert.False(game.State.UtilityFlaskEffects.Ticking);
    }

    [Theory]
    [InlineData("expiry")] [InlineData("death")] [InlineData("replacement")]
    public async Task Harbinger_Silver_queued_start_rejects_ended_or_replaced_original_source(string change)
    {
        using var game = HarbingerGame();
        var effect = HarbingerSilver(game);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        await wire.HoldOutput();
        var start = HarbingerSilverStart(wire, effect);
        Assert.False(start.IsCompleted);
        lock (game.State.Monsters)
        {
            if (change == "death") game.State.PlayerLife = 0;
            UtilityFlasks.EndDue(game.State, effect.Ends + 1, change == "death", null, () => { }, out _);
            if (change == "replacement") game.State.UtilityFlaskEffects.Start(effect with { Buff = 90, ExtraBuff = 91 });
        }
        wire.ReleaseOutput();
        Assert.False(await start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await wire.ReadWire());
    }

    [Fact]
    public async Task Harbinger_Silver_gate_publishes_current_recipient_stats_and_remaining_base_seconds()
    {
        using var game = HarbingerGame();
        var effect = HarbingerSilver(game, seconds: 2);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        await wire.HoldOutput();
        var start = HarbingerSilverStart(wire, effect);
        await Task.Delay(150);
        uint elapsed = Program.Stats.Key("buff_time_passed_+%")!.Value;
        game.State.PlayerStats[elapsed] = 100;
        double before = game.State.Clock.Elapsed.TotalSeconds;
        wire.ReleaseOutput();
        Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(5)));
        double after = game.State.Clock.Elapsed.TotalSeconds;
        var bytes = await wire.ReadWire();
        var (stats, prefix) = HarbingerStatPrefix(bytes);
        Assert.Equal(100, stats[elapsed]);
        float remaining = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(prefix + 23, 4)));
        Assert.InRange(remaining, 2 - (after - effect.Started) * 2 - .02, 2 - (before - effect.Started) + .02);
        Assert.True(remaining < 1.9); // Neither the original duration nor a wall-time deadline is sent.
        Assert.Equal(2, (effect.Ends - effect.Started), 8);
    }

    [Fact]
    public async Task Harbinger_Silver_expiry_after_stat_snapshot_sends_consumed_zero_clear_without_buff_adds()
    {
        using var game = HarbingerGame();
        var conn = game.State;
        var effect = HarbingerSilver(game);
        await using var wire = new ProjectileRuntimeFixture(conn);
        uint rarity = Program.Stats.Key("base_item_found_rarity_+%")!.Value;
        byte[] Snapshot() => ProgramValue<byte[]>("PlayerStatsUpdateAtGate", conn,
            ProgramValue<List<(uint, int)>>("PlayerStatsWithCleared", conn));
        Task<bool> Publish(Func<byte[]?> factory) => ProgramValue<Task<bool>>("SendGenerated",
            "harbinger-expiring-flask", wire.Stream, wire.Ciphers, factory, null);

        conn.PlayerStats[rarity] = 42;
        Assert.True(await Publish(Snapshot));
        Assert.Equal(42, HarbingerStatPrefix(await wire.ReadWire()).Stats[rarity]);
        Assert.Contains(rarity, conn.SentStatKeys);
        conn.PlayerStats.Remove(rarity);
        Assert.True(await Publish(() =>
        {
            lock (conn.Monsters)
            {
                Assert.True(conn.UtilityFlaskEffects.RemainingBase(effect, conn.Clock.Elapsed.TotalSeconds) > 0);
                var stats = Snapshot();
                Assert.DoesNotContain(rarity, conn.SentStatKeys); // Building the snapshot consumes this clear.
                // Force the exact interval after construction without a sleep or a test callback in production.
                return ProgramValue<byte[]>("UtilityFlaskStartPacket", conn, effect, stats, effect.Ends + 1);
            }
        }));
        var bytes = await wire.ReadWire();
        var (cleared, end) = HarbingerStatPrefix(bytes);
        Assert.Equal(0, cleared[rarity]);
        Assert.Equal(bytes.Length, end); // Stats only: neither the source nor infinite Onslaught follows.
        Assert.Equal(0, conn.UtilityFlaskEffects.RemainingBase(effect, effect.Ends + 1));

        Assert.True(await Publish(Snapshot));
        var following = HarbingerStatPrefix(await wire.ReadWire()).Stats;
        Assert.False(following.ContainsKey(rarity)); // A later snapshot cannot repair a discarded one-time clear.
    }
}
