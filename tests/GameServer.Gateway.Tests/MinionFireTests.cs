using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MinionFireTests
{
    [Fact]
    public async Task Infernal_legion_burns_the_minion_and_only_one_overlapping_field_hits_each_enemy()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Infernal Legion Support", 1));
        var item = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, item.Id, row => row with { Links = [2] });
        var first = await Skeleton(f);
        var second = await Skeleton(f);
        first.Monster.Walk.Teleport(100, 100, 0);
        second.Monster.Walk.Teleport(100, 100, 0);
        var target = f.Player.Area.Monsters[900] = Enemy(104);
        var fragile = f.Player.Area.Monsters[902] = new SpawnedMonster("fragile", new(104, 100, 0), 100, 3, 30)
            { Hostile = true, HitAnimations = [new(0, 1)] };
        var far = f.Player.Area.Monsters[901] = Enemy(160);
        const uint burnRate = Stat.SupportMinionInstabilityMinionBaseFireAreaDamagePerMinute;
        first.Own[burnRate] /= 2;
        Game.InfernalLegionTickLocked(f.Player, 1);
        Game.InfernalLegionTickLocked(f.Player, 2);
        Assert.InRange(target.MaxLife - target.Life, 16u, 17u);
        Assert.Equal(far.MaxLife, far.Life);
        Assert.True(fragile.Life > 0 && fragile.Life < fragile.MaxLife);
        Assert.True(fragile.FlinchUntil <= 2, "Burning damage must not stun");
        var immolate = new Dictionary<uint, int>
        {
            [Stat.GlobalMinimumAddedFireDamageVsBurningEnemies] = 10,
            [Stat.GlobalMaximumAddedFireDamageVsBurningEnemies] = 10
        };
        double AddedFire(SpawnedMonster enemy) => Game.WithConditionalSupportDamageLocked(f.Player, immolate,
            false, enemy, DamagePacket.Empty, 2)[DamageType.Fire].Min;
        Assert.Equal(10, AddedFire(target));
        Assert.Equal(0, AddedFire(far));

        uint before = first.Monster.Life;
        double multiplier = Game.MonsterDotTaken(first.Monster, DamageType.Fire, 2);
        Game.GolemDotsLocked(f.Player, first, 1, 2);
        Assert.Equal((uint)Math.Floor(first.Monster.MaxLife * .4 * multiplier), before - first.Monster.Life);

        f.Equip(("Summon Skeletons", 20), ("Infernal Legion Support", 1), ("Brutality Support", 1));
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, item.Id, row => row with { Links = [3] });
        var physicalOnly = await Skeleton(f);
        physicalOnly.Monster.Walk.Teleport(160, 100, 2);
        Game.InfernalLegionTickLocked(f.Player, 3);
        Assert.Equal(far.MaxLife, far.Life);
        uint physicalLife = physicalOnly.Monster.Life;
        Game.GolemDotsLocked(f.Player, physicalOnly, 2, 3);
        Assert.True(physicalOnly.Monster.Life < physicalLife);

        var peer = new Player(f.Player.Layout, 1, 1) { Instance = f.Player.Instance, PlayerLife = 1000, Connection = { PlayerSent = true } };
        foreach (var owner in new[] { f.Player, peer })
            owner.Instance.Presence.AddPlayer(owner);
        f.Player.Summons.Fighters.Remove(second.Id);
        peer.Summons.Fighters[second.Id] = second;
        Game.InfernalLegionTickLocked(peer, 3);
        uint sharedLife = target.Life;
        Game.InfernalLegionTickLocked(peer, 4);
        Game.InfernalLegionTickLocked(f.Player, 4);
        Assert.InRange(sharedLife - target.Life, 16u, 17u);
        first.Own[burnRate] = second.Own[burnRate];
        sharedLife = target.Life;
        Game.InfernalLegionTickLocked(f.Player, 5);
        Game.InfernalLegionTickLocked(peer, 5);
        Assert.InRange(sharedLife - target.Life, 16u, 17u);
    }

    [Fact]
    public async Task Minion_instability_explodes_a_living_minion_once_and_excludes_distant_enemies()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20));
        f.Player.Character.Passives.Add(18663); // Minion Instability on the passive tree.
        var fighter = await Skeleton(f);
        fighter.Monster.Walk.Teleport(100, 100, 0);
        var near = f.Player.Area.Monsters[900] = Enemy(105);
        var far = f.Player.Area.Monsters[901] = Enemy(160);
        int expected = (int)Math.Round(fighter.Monster.MaxLife * .33);
        Assert.False(Game.MinionInstabilityLocked(f.Player, fighter, 1));
        fighter.Monster.Life = fighter.Monster.MaxLife / 2;
        Assert.True(Game.MinionInstabilityLocked(f.Player, fighter, 1));
        Assert.True(fighter.Monster.Dead);
        Assert.Equal(expected, (int)(near.MaxLife - near.Life));
        Assert.Equal(far.MaxLife, far.Life);
        uint remaining = near.Life;
        Assert.False(Game.MinionInstabilityLocked(f.Player, fighter, 2));
        Assert.Equal(remaining, near.Life);
    }

    private static SpawnedMonster Enemy(int x) => new("target", new(x, 100, 0), 100, 3, 100000)
        { Hostile = true };

    private static async Task<GolemState.Golem> Skeleton(MinionUtilityTests.Fixture f)
    {
        ushort source = Game.GemSkillEffects(f.Player).Single().Id;
        uint id = await Game.SummonFighter(f.Player, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        return f.Player.Summons.Fighters[id];
    }
}
