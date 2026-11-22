using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class AcrobaticsSpellDodgeTests
{
    [Fact]
    public void Acrobatics_converts_uncapped_and_conditional_suppression_and_dodges_spell_hits()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        uint suppression = Program.Stats.Key("base_spell_suppression_chance_%")!.Value;
        uint fullLife = Program.Stats.Key("spell_suppression_chance_%_while_on_full_life")!.Value;
        uint acrobatics = Program.Stats.Key("keystone_acrobatics")!.Value;
        var stats = new Dictionary<uint, int> { [suppression] = 61 };
        var character = Game.SkillsFor(conn).ClassFor(conn.ClassRow);
        CharacterStats.Pools Pools() => CharacterStats.For(character, 90,
            Game.PassiveBonus(stats, new WeaponProfile(false, false, false, false, false), default));

        conn.Pools = Pools();
        Assert.Equal((61, 0), Game.SpellDefences(conn));
        stats[acrobatics] = 1;
        conn.Pools = Pools();
        Assert.Equal((0, 30), Game.SpellDefences(conn));
        Assert.Equal(0, Game.Suppression(conn).Chance);

        stats[fullLife] = 10;
        conn.Pools = Pools();
        conn.PlayerLife = conn.PlayerMaxLife;
        Assert.Equal((0, 35), Game.SpellDefences(conn));
        conn.PlayerLife--;
        Assert.Equal((0, 30), Game.SpellDefences(conn));
        stats[suppression] = 160;
        conn.Pools = Pools();
        Assert.Equal((0, 75), Game.SpellDefences(conn));
        stats[suppression] = 61;
        conn.Pools = Pools();

        var enemy = new SpawnedMonster("spell caster", new(100, 100, 0), 50, 6, 1000);
        var attack = new MonsterAttack(0x2909, 1, 20, DamagePacket.Of(DamageType.Fire, 100, 100)) { Spell = true };
        var at = conn.Walk.Position(0);
        uint life = conn.PlayerLife;
        conn.IncomingHitRandom = new DodgeRoll(29);
        lock (conn.Monsters)
            Assert.Null(Game.CommitMonsterHitLocked("acrobatics-test", conn, 700, enemy, attack, 1, at, at, checkRange: false));
        Assert.Equal(life, conn.PlayerLife);

        conn.IncomingHitRandom = new DodgeRoll(30);
        lock (conn.Monsters)
            Assert.NotNull(Game.CommitMonsterHitLocked("acrobatics-test", conn, 700, enemy, attack, 2, at, at, checkRange: false));
        Assert.True(conn.PlayerLife < life);
    }

    sealed class DodgeRoll(int roll) : Random(42)
    {
        public override int Next(int minValue, int maxValue) =>
            minValue == 0 && maxValue == 100 ? roll : base.Next(minValue, maxValue);
    }
}
