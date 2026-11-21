using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Awakened supports' extra mechanics: Awakened Brutality's Crush, Awakened Spell Echo's double damage on its final
// repeat, and Awakened Generosity's per-aura increased effect.
public sealed class AwakenedSupportTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    // Crush: a hit's chance crushes for 4 seconds, and a crushed monster's physical damage reduction is 15% lower.
    [Fact]
    public void Crush_lowers_physical_damage_reduction_for_four_seconds()
    {
        var monster = new SpawnedMonster("crushed", new(105, 100, 0), 0, 0, 1000)
        {
            Mods = new([], [], new(new Dictionary<string, int> { ["base_additional_physical_damage_reduction_%"] = 50 })),
        };
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        Assert.Equal(50, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 1));

        Game.HitDebuffsOnHitLocked(monster, Stats(("chance_to_crush_on_hit_%", 100)), spell: false, critical: false, now: 10);
        Assert.Equal(14, monster.CrushedUntil);
        Assert.Equal(65, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 11));
        Assert.Equal(50, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 14));

        // A miss (0% chance) crushes nothing.
        var lucky = new SpawnedMonster("lucky", new(105, 100, 0), 0, 0, 1000);
        Game.HitDebuffsOnHitLocked(lucky, Stats(("chance_to_crush_on_hit_%", 0)), spell: false, critical: false, now: 10);
        Assert.Equal(double.NegativeInfinity, lucky.CrushedUntil);
    }

    // Awakened Spell Echo's double damage belongs to the final repeat only; Awakened Generosity's increased effect
    // scales the aura it supports.
    [Fact]
    public void Final_repeat_double_damage_and_aura_support_effect_apply()
    {
        var echo = Stats(("spell_echo_plus_chance_double_damage_%_final", 100));
        Assert.Equal(100, GamblerModifiers.From(_ => 0).ForSkill(echo, spell: true, finalRepeat: true).DoubleDamageChance);
        Assert.Equal(0, GamblerModifiers.From(_ => 0).ForSkill(echo, spell: true).DoubleDamageChance);
        Assert.Equal(0, GamblerModifiers.From(_ => 0).ForSkill(echo, spell: false, finalRepeat: true).DoubleDamageChance);

        using var battle = new ArrowRainAndMirageTests.Battle("Frostbolt");
        var conn = battle.Conn;
        var items = Game.ItemsOf(conn);
        var host = items.EquipmentSnapshot().Single();
        var awakened = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemSpellEchoPlus")!);
        items.Change(host.Row, host.Item.Id, item => item with
        { Sockets = [item.Sockets[0], new(SocketColour.White, awakened)], Links = [2] });
        var gem = Game.GemSkillEffects(conn).Single();
        uint chance = Program.Stats.Key("spell_echo_plus_chance_double_damage_%_final")!.Value;

        uint ProjectileDamage(int repeat, uint left)
        {
            var use = Game.CaptureGemProjectile(conn, battle.Session, gem.Id, gem, (130, 100), new(0),
                repeat: repeat, repeatsLeft: left);
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(20, shot.SkillStats[chance]);
            // Fixed damage and a guaranteed roll isolate whether this release qualifies for the bonus.
            var fixedShot = shot with { Hit = DamagePacket.Of(DamageType.Cold, 100, 100), CriticalChance = 0,
                SkillStats = new Dictionary<uint, int>(shot.SkillStats) { [chance] = 100 } };
            uint life = battle.Enemy.Life;
            battle.Session.Runtime.Release(use with { Payload = fixedShot });
            battle.AdvanceBy(3);
            return life - battle.Enemy.Life;
        }

        Assert.Equal(100u, ProjectileDamage(0, 0));
        Assert.Equal(100u, ProjectileDamage(0, 2));
        Assert.Equal(100u, ProjectileDamage(1, 1));
        Assert.Equal(200u, ProjectileDamage(2, 0));

        var aura = new AuraCatalog.Aura(1, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1, 1, 0, 0,
            Level: 20, Quality: 0);
        var plain = AuraCatalog.Bonus([aura], 0, Program.Skills, Program.Stats).AttackSpeedPct;
        var supported = AuraCatalog.Bonus([aura with { SupportEffectPct = 100 }], 0, Program.Skills, Program.Stats);
        Assert.True(plain > 0);
        Assert.InRange(supported.AttackSpeedPct, plain * 2 - 1, plain * 2 + 1);
    }
}
