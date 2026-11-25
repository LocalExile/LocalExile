using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SecondarySkillExecutionTests
{
    static ushort Equip(MinionUtilityTests.Fixture f, params string[] names)
    {
        f.Equip(names.Select(n => (n,4)).ToArray());
        var host=Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,i=>i with {Links=[(byte)names.Length]});
        return Game.GemSkillEffects(f.Player).Single().Id;
    }

    static IDisposable OverrideStat(Player player, uint stat, int value) =>
        GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
        {
            if (!ReferenceEquals(p, player)) return sources;
            var copy = new Dictionary<uint, int>(sources) { [stat] = value };
            return copy;
        });

    [Fact]
    public async Task Charged_supports_contribute_their_distinct_critical_stats_to_proxy_projectiles()
    {
        using var f = new MinionUtilityTests.Fixture();
        var session = Game.ProjectilesFor(f.Player);
        try
        {
            using var vsBlinded = OverrideStat(f.Player, Game.VsBlindedCriticalStat, 50);
            Assert.Equal(Capture("Blastchain Mine Support").CriticalChance,
                Capture("Blastchain Mine Support", "Charged Mines Support").CriticalChance);
            Assert.Equal(Capture("Trap Support").CriticalMultiplier,
                Capture("Trap Support", "Charged Traps Support").CriticalMultiplier);
            GrantPowerCharges(f.Player);
            var ordinaryMine = Capture("Blastchain Mine Support");
            var chargedMine = Capture("Blastchain Mine Support", "Charged Mines Support");
            var ordinaryTrap = Capture("Trap Support");
            var chargedTrap = Capture("Trap Support", "Charged Traps Support");
            int baseChance = Program.Skills.SpellCritChance(chargedMine.Effect, chargedMine.Level)!.Value;
            Assert.Equal(ordinaryMine.CriticalChance + baseChance * 40 / 100, chargedMine.CriticalChance);
            Assert.Equal(ordinaryMine.VsBlindedCriticalChance + baseChance * 40 / 100, chargedMine.VsBlindedCriticalChance);
            Assert.Equal(ordinaryMine.CriticalMultiplier, chargedMine.CriticalMultiplier);
            Assert.Equal(ordinaryTrap.CriticalChance, chargedTrap.CriticalChance);
            Assert.Equal(ordinaryTrap.CriticalMultiplier + 30, chargedTrap.CriticalMultiplier);
        }
        finally { session.Invalidate(); await session.OutputCompleted; }

        ProjectileOffense Capture(params string[] supports)
        {
            Equip(f, ["Fireball", .. supports]);
            var gem = Game.GemSkillEffects(f.Player).Single();
            var use = Game.CaptureGemProjectile(f.Player, session, gem.Id, gem, (120, 100), new(0), from: (100, 100));
            return Assert.IsType<ProjectileOffense>(use.Payload);
        }
    }

    [Fact]
    public void Direct_placed_hits_roll_critical_chance_and_apply_the_trap_multiplier()
    {
        var critical = new FixedCriticalRoll();
        using var f = new MinionUtilityTests.Fixture(critical);
        GrantPowerCharges(f.Player);
        Assert.Equal(850u, Detonate("Fire Trap", false));
        Assert.Equal(820u, Detonate("Fire Trap", false, "Charged Traps Support"));

        ushort ordinaryMine = Equip(f, "Stormblast Mine");
        critical.Roll = Game.SupportedCriticalChance(f.Player, ordinaryMine);
        Assert.Equal(900u, Detonate("Stormblast Mine", true));
        Assert.Equal(850u, Detonate("Stormblast Mine", true, "Charged Mines Support"));

        using var noCrit = OverrideStat(f.Player, Stat.GlobalCannotCrit, 1);
        critical.Roll = 0;
        Assert.Equal(900u, Detonate("Fire Trap", false, "Charged Traps Support"));

        uint Detonate(string name, bool mine, params string[] supports)
        {
            // Each comparison starts on fresh ground. Fire Trap adds damage against a burning target.
            f.Player.Placed.Grounds.Clear();
            ushort source = Equip(f, [name, .. supports]);
            var gem = Game.SupportedSkill(f.Player, source);
            var placed = new PlacedSkills.Placed(777, source, gem.Effect, (100, 100), mine, 0, 10,
                DamagePacket.Of(DamageType.Fire, 100, 100), gem.Stats, 0, 0, false, 0);
            f.Player.Area.Monsters[900] = new("victim", new(102, 100, 0), 0, 0, 1000) { Hostile = true };
            PlacedHit(f, placed);
            return f.Player.Area.Monsters[900].Life;
        }
    }

    static void GrantPowerCharges(Player player)
    {
        var charges = Game.ChargesOf(player);
        charges.Admit(0, [new ChargeEvent.Grant(ChargeType.Power, 2, "test")], _ => 0, () => default,
            alive: true, new Random(1));
        Assert.Equal(2, charges.Effective(ChargeType.Power));
        player.Pools = Game.PoolsFor(player, 100);
    }

    sealed class FixedCriticalRoll : Random
    {
        public int Roll { get; set; }
        public override int Next(int maxValue) => Roll;
    }

    static void PlacedHit(MinionUtilityTests.Fixture f, PlacedSkills.Placed placed)
    {
        var method = typeof(Game).GetMethod("PlacedDamageLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var records = Activator.CreateInstance(method.GetParameters()[^1].ParameterType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [f.Player, null], null);
        method.Invoke(null, [f.Player, placed, 1d, default(KeyValuePair<uint, SpawnedMonster>), records]);
    }

    [Fact]
    public void Bear_trap_hits_one_nearby_enemy_without_splashing_other_enemies_or_allies()
    {
        using var f = new MinionUtilityTests.Fixture(new FixedCriticalRoll { Roll = 9999 });
        ushort source = Equip(f, "Bear Trap"); var gem = Game.SupportedSkill(f.Player, source);
        var placed = new PlacedSkills.Placed(777, source, gem.Effect, (100, 100), false, 0, 10,
            DamagePacket.Of(DamageType.Physical, 100, 100), gem.Stats, 0, 0, false, 0);
        foreach (var (id, x, hostile) in new[] { (900u, 104, true), (901u, 102, true), (902u, 103, false), (903u, 140, true) })
            f.Player.Area.Monsters[id] = new("victim", new(x, 100, 0), 0, 0, 1000) { Hostile = hostile };
        PlacedHit(f, placed);
        Assert.Equal(900u, f.Player.Area.Monsters[901].Life);
        Assert.Equal(1000u, f.Player.Area.Monsters[900].Life); Assert.Equal(1000u, f.Player.Area.Monsters[902].Life);
        Assert.Equal(1000u, f.Player.Area.Monsters[903].Life);
        var victim = f.Player.Area.Monsters[901];
        Assert.True(victim.Walk.MovementBlocked(2.49));
        Assert.False(victim.Walk.MovementBlocked(2.5));
        Assert.Equal(1, Game.MonsterActionFactor(victim, 1.1));
        Assert.False(victim.Invulnerable);
        victim.Walk.Order(120, 100, 1.5);
        Assert.Equal((102, 100), victim.Walk.Destination);
        Assert.Equal(.2, Game.BearTrapWalkFactor(victim, 2.5), 8);
        Assert.Equal(.6, Game.BearTrapWalkFactor(victim, 4), 8);
        Assert.Equal(1, Game.BearTrapWalkFactor(victim, 5.5));
        Assert.Equal(5, Game.BearTrapRootSeconds(1000, 1000));
        Assert.Equal(.15, Game.BearTrapRootSeconds(10, 1000), 8);
        var packet = DamagePacket.Of(DamageType.Fire, 100, 100);
        victim.TakenFromPlayer = (20, 0, 0);
        Assert.Equal(120, Game.MonsterTakesMitigated(victim, packet, false, 2));
        Assert.Equal(145, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 2));
        Assert.Equal(120, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 5.5));
        victim.Ignites.Add(1, 1, 6);
        Game.InflictedLocked(f.Player, victim, Game.AilmentType.Ignite, 7);
        Game.KeepPerAilmentTypeTakenLocked(f.Player, victim, 10);
        Assert.Equal(130, Game.MonsterTakesMitigated(victim, packet, false, 2));
        Assert.Equal(155, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 2));
        Assert.Equal(130, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 5.5));
        var buffs = Game.MonsterAilmentChangesLocked(f.Player, 1.1);
        Assert.Contains(buffs, p => p.Log == "Bear Trap debuff");
        // The client walks by movement_velocity_+%, so the slow is set on Area.Stats and tracked on the monster.
        Assert.True(victim.MaimedMovementShown);
        Assert.Equal(-80, victim.MaimedMovementShownPct);
        Assert.DoesNotContain(Game.MonsterAilmentChangesLocked(f.Player, 1.2), p => p.Log == "Bear Trap debuff");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Player, 2.5), p => p.Log == "Bear Trap debuff changes");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Player, 5.5), p => p.Log == "Bear Trap debuff changes");
        Assert.Equal(1, victim.Ailments.WalkFactor);
        var immune = new SpawnedMonster("movement floor", new(100, 100, 20), 0, 0, 1000)
            { EncounterStats = new Dictionary<uint, int> { [Stat.MovementSpeedCannotBeReducedBelowBase] = 1 } };
        Game.BearTrapOnHitLocked(immune, 100, 1, _ => 0, Own);
        Assert.False(immune.Walk.MovementBlocked(1.1));
        Assert.Equal(1, Game.BearTrapWalkFactor(immune, 1.1));
        int Own(uint name) => gem.Stats.GetValueOrDefault(name);
    }
}
