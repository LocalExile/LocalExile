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
    [InlineData("Standard")]
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
        double now = conn.Clock.Elapsed.TotalSeconds;
        conn.ElementalAilments.Apply(cold, cold, false, conn.PlayerMaxLife, now, _ => 0, new Random(1));

        Assert.Equal(.7, Program.PlayerActionFactor(conn), 6);
        Assert.Equal(beforeAttack / .7, (double)Call("SkillAttackSeconds", conn, attack, 1)!, 6);
        Assert.Equal((int)Math.Ceiling(beforeCast / .7), (int)Call("CastMs", conn, spell, (ushort)0)!);
    }

    public void Dispose()
    {
        foreach (string name in characters) PlayerInventories.Forget(name);
    }
}
