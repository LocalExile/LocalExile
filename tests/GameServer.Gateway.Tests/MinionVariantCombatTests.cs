using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MinionVariantCombatTests
{
    [Fact]
    public async Task Revenants_consume_a_nearby_minion_to_heal_without_becoming_a_manual_recast_variant()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Reaper of Revenants", 20), ("Summon Skeletons", 20));
        var reaper = await Spawn(f, "SummonReaperAltX");
        var skeleton = await Spawn(f, "SummonSkeletons");
        skeleton.Monster.Walk.Teleport(103, 100, 0);
        uint injured = reaper.Monster.Life = reaper.Monster.MaxLife / 2;

        Step(f, reaper);
        var action = Assert.IsType<GolemState.Action>(reaper.Pending);
        Assert.Equal(12032, action.Ability.Effect);
        Assert.Equal(skeleton.Id, action.Target);
        Game.GolemAbilityContactLocked(f.Player, reaper, action, 1);
        Assert.True(skeleton.Monster.Dead);
        Assert.True(reaper.Monster.Life > injured);
    }

    [Fact]
    public async Task Guardian_and_zombie_variants_select_their_own_attacks()
    {
        foreach (var (name, effect, expected) in new[]
            { ("Animate Guardian", "AnimateGuardian", 406),
              ("Animate Guardian of Smiting", "AnimateGuardianAltX", 9638) })
        {
            using var f = new MinionUtilityTests.Fixture();
            f.Equip((name, 20));
            var item = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!);
            uint drop = f.Player.Instance.Ground.Add(new WorldItemObject(100, 100, item));
            var guardian = await Spawn(f, effect, drop);
            f.Player.Area.Monsters[900] = Enemy(103);
            Step(f, guardian);
            Assert.Equal(expected, guardian.Pending?.Ability.Effect);
            if (expected == 9638)
            {
                int x = 101 + (int)guardian.Pending!.Ability.Attack.Range;
                f.Player.Area.Monsters[900].Walk.Teleport(x, 100, 1);
                f.Player.Area.Monsters[901] = Enemy(x + 3);
                Game.GolemAbilityContactLocked(f.Player, guardian, guardian.Pending!, 1);
                Assert.True(f.Player.Area.Monsters[900].Life < 100000);
                Assert.True(f.Player.Area.Monsters[901].Life < 100000);
            }
        }
        foreach (var (name, effect, expected) in new[]
            { ("Raise Zombie", "RaiseZombie", 406), ("Raise Zombie of Slamming", "RaiseZombieAltX", 5590) })
        {
            using var f = new MinionUtilityTests.Fixture();
            f.Equip((name, 20));
            f.Player.Area.Monsters[900] = new("corpse", new(100, 100, 0), 100, 3, 1000)
                { Hostile = true, Dead = true, Life = 0, Level = 70, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie") };
            var zombie = await Spawn(f, effect, 900);
            f.Player.Area.Monsters[901] = Enemy(103);
            Step(f, zombie);
            Assert.Equal(expected, zombie.Pending?.Ability.Effect);
            zombie.Pending = null;
            zombie.Monster.BusyUntil = 0;
            zombie.Charges[5590] = new([10]);
            Step(f, zombie);
            Assert.Equal(406, zombie.Pending?.Ability.Effect);
        }
    }

    private static SpawnedMonster Enemy(int x) => new("target", new(x, 100, 0), 100, 3, 100000)
        { Hostile = true };

    private static async Task<GolemState.Golem> Spawn(MinionUtilityTests.Fixture f, string effect, uint target = 0)
    {
        ushort source = Game.GemSkillEffects(f.Player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
        uint id = await Game.SummonFighter(f.Player, source,
            Game.SummonData.Find(effect)!, (100, 100), target);
        return f.Player.Summons.Fighters[id];
    }

    private static void Step(MinionUtilityTests.Fixture f, GolemState.Golem fighter)
    {
        f.Player.Walk.Teleport(100, 100, 0);
        fighter.Monster.Walk.Teleport(100, 100, 0);
        Game.GolemStepLocked(f.Player, fighter, 1);
    }
}
