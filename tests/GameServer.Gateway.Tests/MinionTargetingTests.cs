using System.Reflection;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionTargetingTests
{
    static readonly BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static SpawnedMonster Enemy(string label, int x) =>
        new(label, new(x, 100, 0), 100, 3, 100000) { Hostile = true, Level = 70 };

    [Fact]
    public async Task A_lurking_monster_is_a_minion_target_and_stays_one_after_a_hit_makes_it_rise()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Zombie", 20));
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        f.Player.Area.Monsters[901] = new("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = variety, Level = 70 };
        var fighter = f.Player.Summons.Fighters[await f.Summon("RaiseZombie", 901)];
        f.Player.Walk.Teleport(100, 100, 0);
        fighter.Monster.Walk.Teleport(100, 100, 0);

        // Waiting to rise: busy until it does (MonsterEmerge.AddLocked), and still a target for the fighter and the beast path.
        var lurking = Enemy("lurking", 108);
        Assert.True(MonsterEmerge.AddLocked(f.Player, 910, lurking, EmergeKind.Lurking, unrisen: true));
        Assert.True(double.IsPositiveInfinity(lurking.BusyUntil));
        Game.GolemStepLocked(f.Player, fighter, 1);
        Assert.Equal(910u, fighter.Target);
        var beast = new SpawnedMonster("beast", new(100, 100, 0), 100, 3, 1000) { Attack = new(0x4000, 1, 20, DamagePacket.Empty) };
        Assert.Equal(910u, Game.MinionTargetLocked(f.Player, 800, beast, (100, 100), (100, 100), 1)?.Id);

        // A hit makes it rise (the watch's RiseLocked); it is the target before and after.
        lurking.Life -= 1;
        var risen = Assert.Single(new MonsterEmerge().RiseLocked(f.Player, (100, 100), 2, out _));
        Assert.Same(lurking, risen.Monster);
        Assert.DoesNotContain(910u, f.Player.Area.EmergeGround.Lurking);
        Assert.True(Game.MinionMayTarget(lurking));
        Game.GolemStepLocked(f.Player, fighter, 2);
        Assert.Equal(910u, fighter.Target);
    }

    [Fact]
    public async Task Held_asleep_and_immune_monsters_are_a_target_on_none_of_the_four_minion_paths()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Zombie", 20));
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        f.Player.Area.Monsters[901] = new("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = variety, Level = 70 };
        var fighter = f.Player.Summons.Fighters[await f.Summon("RaiseZombie", 901)];
        f.Player.Walk.Teleport(100, 100, 0);
        fighter.Monster.Walk.Teleport(100, 100, 0);
        var beast = new SpawnedMonster("beast", new(100, 100, 0), 100, 3, 1000) { Attack = new(0x4000, 1, 20, DamagePacket.Empty) };
        var skitterDefinition = new GolemCatalog.Definition(0, "SummonSkitterbots", "SkitterCold",
            "Metadata/Monsters/Skitterbot/SkitterbotCold", 0, [], false, false, IsGolem: false);
        var skitter = new GolemState.Golem(902, new("skitter", new(100, 100, 0), 100, 3, 1000), skitterDefinition, 0);
        var mirage = typeof(Game).GetMethod("PickMirageWarriorTargetLocked", Any)!;
        var skitterStep = typeof(Game).GetMethod("SkitterStepLocked", Any)!;

        uint? Fighter() { fighter.Target = 0; Game.GolemStepLocked(f.Player, fighter, 1); return fighter.Target == 0 ? null : fighter.Target; }
        uint? Beast() => Game.MinionTargetLocked(f.Player, 800, beast, (100, 100), (100, 100), 1)?.Id;
        uint? Mirage() => mirage.Invoke(null, [f.Player, (100, 100), 0u, 1d]) is ITuple picked ? (uint)picked[0]! : null;
        // Where the skitter walks tells whom it chose, a cell next to that monster; staying by its owner (no target) is next to
        // none of them. All of them are farther than the aura radius it stops at.
        uint? Skitter()
        {
            skitter.Monster.Walk.Teleport(100, 100, 0); skitter.Monster.LastTarget = null; skitter.NextMove = 0;
            skitterStep.Invoke(null, [f.Player, skitter, (100, 100), (100, 100), 1d]);
            var to = skitter.Monster.Walk.Destination;
            var (id, near) = f.Player.Area.Monsters.Where(p => p.Key is >= 910 and <= 914).Select(p => (p.Key, Game.Dist(p.Value.Walk.Position(1), to))).MinBy(t => t.Item2);
            return near <= 10 ? id : null;
        }

        // Held by a script (infinite BusyUntil and ScriptHeld), asleep, immune: all nearer than the plain monster.
        f.Player.Area.Monsters[910] = Enemy("held", 85);
        f.Player.Area.Monsters[910].BusyUntil = double.PositiveInfinity; f.Player.Area.Monsters[910].ScriptHeld = true;
        f.Player.Area.Monsters[911] = Enemy("asleep", 84); f.Player.Area.Monsters[911].Asleep = true;
        f.Player.Area.Monsters[912] = Enemy("immune", 83); f.Player.Area.Monsters[912].DamageImmune = true;
        f.Player.Area.Monsters[913] = Enemy("plain", 125);
        Assert.Equal(913u, Fighter());
        Assert.Equal(913u, Beast());
        Assert.Equal(913u, Mirage());
        Assert.Equal(913u, Skitter());

        // With only those three left, none of the paths has a target.
        f.Player.Area.Monsters.Remove(913);
        Assert.Null(Fighter());
        Assert.Null(Beast());
        Assert.Null(Mirage());
        Assert.Null(Skitter());

        // A monster that is busy forever only because it winds up a skill (no ScriptHeld) is a target.
        f.Player.Area.Monsters[914] = Enemy("winding up", 126);
        f.Player.Area.Monsters[914].BusyUntil = double.PositiveInfinity;
        Assert.Equal(914u, Fighter());
        Assert.Equal(914u, Beast());
        Assert.Equal(914u, Mirage());
        Assert.Equal(914u, Skitter());
        f.Player.Area.Monsters.Remove(914);

        // A Mirage Warrior keeps a target only while it stays one: a plain monster that falls asleep is dropped.
        f.Player.Area.Monsters[913] = Enemy("plain", 125);
        Assert.Equal(913u, Mirage());
        f.Player.Area.Monsters[913].Asleep = true;
        Assert.True(mirage.Invoke(null, [f.Player, (100, 100), 913u, 1d]) is null);
    }
}
