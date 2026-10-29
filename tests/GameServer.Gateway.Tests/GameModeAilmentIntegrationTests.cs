using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class GameModeAilmentIntegrationTests : IDisposable
{
    readonly List<string> characters = [];

    static object? Call(string name, params object?[] args) => typeof(Program)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    ClientState Connection(string league)
    {
        TestSaves.Ensure();
        string name = "ModeAilment" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        var mode = LeagueCatalog.Default.Find(league)!;
        var character = new Character(Guid.NewGuid(), Guid.NewGuid(), name, CharacterClass.Witch, 1,
            league, "1_1_2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        new SqliteAccountStore(Saves.Db).AddCharacter(character);
        CharacterSaves.Write(name, new([]));
        var conn = new ClientState(new(ushort.MaxValue, "1_1_2", 300, 300, 10, 10, null, []), 1, 1)
        {
            Character = name, CharacterId = character.Id, ClassRow = SkillCatalog.WitchRow, League = league,
            Hardcore = mode.Hardcore, Ssf = mode.Ssf, Ruthless = mode.Ruthless,
            PlayerLife = 1, PlayerMana = 1,
        };
        conn.Pools = Program.PoolsFor(conn, 1);
        return conn;
    }

    [Theory]
    [InlineData("Hardcore", "Standard")]
    [InlineData("SSF Hardcore", "SSF Standard")]
    [InlineData("Hardcore Ruthless", "Void")]
    [InlineData("SSF Hardcore Ruthless", "Void")]
    [InlineData("Standard", "Standard")]
    [InlineData("Ruthless", "Ruthless")]
    public async Task Lethal_ignite_commits_mode_death_before_output_and_runs_death_effects_once(
        string league, string destination)
    {
        var conn = Connection(league);
        var boss = new SpawnedMonster("mode ailment boss", new(12, 10, 0), 0, 2, 100)
        { Variety = Program.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")! };
        boss.Life = 25;
        conn.Monsters.Add(42, boss);
        uint expectedBossLife = conn.Ruthless
            ? RuthlessGameplay.BossLifeAfterDeath(25, 100, Program.RuthlessSettings.Current.CampaignBossLifeRecoveryFraction)
            : 25;
        var fire = DamagePacket.Of(DamageType.Fire, 1000, 1000);
        conn.ElementalAilments.Apply(fire, fire, true, conn.PlayerMaxLife, 0, _ => 0, new Random(1));
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();

        lock (conn.Monsters)
            Program.AdvancePlayerAilmentsLocked(conn, 0, 1, Program.CharacterPlayerStats(conn));

        // Observe durable death before even invoking the asynchronous notification path.
        Assert.Equal(0u, conn.PlayerLife);
        Assert.Equal(conn.Hardcore, conn.HardcoreDeathRecorded);
        var store = new SqliteAccountStore(Saves.Db);
        var saved = store.FindCharacter(conn.Character!)!;
        Assert.Equal(destination, saved.League);
        Assert.Equal(conn.Hardcore, saved.HardcoreDeath is not null);
        if (conn.Hardcore)
        {
            Assert.Equal(league, saved.HardcoreDeath!.SourceLeague);
            Assert.Equal(conn.Ruthless, saved.HardcoreDeath.PermanentlyDead);
        }

        var death = (Task)Call("PlayerDiedAtEpoch", "mode-ailment", wire.Stream, wire.Ciphers,
            conn, "lethal ignite", (int?)conn.IncomingHitEpoch)!;
        Assert.False(death.IsCompleted);
        Assert.Equal(expectedBossLife, boss.Life);
        wire.ReleaseOutput();
        await death.WaitAsync(TimeSpan.FromSeconds(5));
        await (Task)Call("PlayerDied", "mode-ailment", wire.Stream, wire.Ciphers, conn, "duplicate")!;
        byte[] bytes = await wire.ReadWire();
        byte[] packet = InstanceProtocol.DeathScreen(Program.DeathScreenFlags(conn));
        Assert.Single(Enumerable.Range(0, bytes.Length - packet.Length + 1),
            i => bytes.AsSpan(i, packet.Length).SequenceEqual(packet));
        Assert.Equal(expectedBossLife, boss.Life);
        Assert.Equal(saved, store.FindCharacter(conn.Character!));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("transfer")]
    [InlineData("stale")]
    public async Task Retired_or_stale_death_notifications_cannot_commit_a_Hardcore_death(string state)
    {
        var conn = Connection("Hardcore");
        conn.PlayerLife = 0;
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();
        conn.Closed = state == "closed";
        conn.LeavingArea = state == "transfer";
        int expectedEpoch = state == "stale" ? conn.IncomingHitEpoch - 1 : conn.IncomingHitEpoch;

        await ((Task)Call("PlayerDiedAtEpoch", "retired-mode", wire.Stream, wire.Ciphers,
            conn, "stale death", (int?)expectedEpoch)!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(conn.HardcoreDeathRecorded);
        var saved = new SqliteAccountStore(Saves.Db).FindCharacter(conn.Character!)!;
        Assert.Equal("Hardcore", saved.League);
        Assert.Null(saved.HardcoreDeath);
        Assert.Equal(-1, conn.DeathNotificationEpoch);
        Assert.Equal(0, wire.Available);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Ruthless")]
    public void Chill_slows_attack_and_spell_timers_once_in_both_modes(string league)
    {
        var conn = Connection(league);
        int attack = Program.SkillsFor(conn).EffectRow("HeavyStrike");
        int spell = Program.SkillsFor(conn).EffectRow("Fireball");
        Assert.True(Program.SkillsFor(conn).IsAttack(attack));
        Assert.True(Program.SkillsFor(conn).IsSpell(spell));
        double beforeAttack = (double)Call("SkillAttackSeconds", conn, attack, 1)!;
        int beforeCast = (int)Call("CastMs", conn, spell, (ushort)0)!;
        Assert.True(beforeCast > 0);
        var cold = DamagePacket.Of(DamageType.Cold, conn.PlayerMaxLife, conn.PlayerMaxLife);
        conn.ElementalAilments.Apply(cold, cold, false, conn.PlayerMaxLife, 0, _ => 0, new Random(1));

        Assert.Equal(.7, Program.PlayerActionFactor(conn), 6);
        Assert.Equal(beforeAttack / .7, (double)Call("SkillAttackSeconds", conn, attack, 1)!, 6);
        Assert.Equal((int)Math.Ceiling(beforeCast / .7), (int)Call("CastMs", conn, spell, (ushort)0)!);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Ruthless")]
    public async Task Frozen_object_skill_cannot_start_an_action_or_write_output_in_either_mode(string league)
    {
        var conn = Connection(league);
        var cold = DamagePacket.Of(DamageType.Cold, conn.PlayerMaxLife, conn.PlayerMaxLife);
        conn.ElementalAilments.Apply(cold, cold, true, conn.PlayerMaxLife, 0, _ => 0, new Random(1));
        Assert.Equal(0, conn.ElementalAilments.ActionFactor(0));
        int action = conn.ActionCount;
        var target = new SpawnedMonster("frozen target", new(12, 10, 0), 0, 2, 100);
        conn.Monsters.Add(42, target);
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();
        var use = new InstanceTransfer.UseSkillOnObject(42, SkillCatalog.FirstSkillId, 0, 0);
        await ((Task)Call("OnUseObject", "frozen-mode", wire.Stream, wire.Ciphers, conn, use, 0d)!)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(action, conn.ActionCount);
        Assert.Equal(100u, target.Life);
        Assert.Equal(0, wire.Available);
    }

    public void Dispose()
    {
        foreach (string name in characters) PlayerInventories.Forget(name);
    }
}
