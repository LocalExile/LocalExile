using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EnergyShieldNotDelayedTests
{
    [Fact]
    public async Task Vaal_Discipline_protects_nearby_players_and_their_minions_from_recharge_delay()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var caster = f.Player;
        caster.God = true;
        caster.Connection.PlayerObjectCreated = true;
        caster.Walk.Teleport(100, 100, 0);
        caster.ProjectileRuntimeState = f.Session;
        f.Equip(("Vaal Discipline", 1));
        var skill = Assert.Single(Game.GemSkillEffects(caster), gem => Program.Skills.EffectId(gem.Effect) == "VaalDiscipline");
        var budget = Assert.NotNull(Program.Skills.VaalBudget(skill.Effect, skill.Level));
        var ally = new Player(caster.Layout, 2, 1)
        {
            Instance = caster.Instance,
            Character = { Name = "DisciplineAlly" + Guid.NewGuid().ToString("N"), Experience = caster.Character.Experience },
            PlayerLife = 10000,
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = caster.LeagueModule,
            Connection = { PlayerSent = true, PlayerObjectCreated = true },
        };
        ally.Walk.Teleport(110, 100, 0);
        caster.Area.Grid.Set(new(caster.Id), (100, 100));
        caster.Area.Grid.Set(new(ally.Id), (110, 100));
        caster.PeerTemplate = new(caster.Character.Name!, 0, 100, 100, caster.PlayerLife, caster.PlayerMana);
        caster.Instance.AddPlayer(ally);
        var monster = new SpawnedMonster("ally", new(115, 100, 0), 60, 8, 10000) { Hostile = false };
        var definition = new GolemCatalog.Definition(-1, "test", "Spectre", "test", 0, [], false, false, false);
        var minion = new GolemState.Golem(44, monster, definition, 0);
        caster.Area.Monsters[minion.Id] = monster;
        caster.Area.Grid.Set(new(minion.Id), (115, 100));
        ally.Summons.Fighters[minion.Id] = minion;
        var allyUpdates = new PlayerUpdatesState(f.Lifetime, Game.PlayerUpdatesSchedule);
        ally.PlayerUpdatesState = allyUpdates;
        const uint protection = Stat.EnergyShieldRechargeNotDelayedByDamage;
        try
        {
            Assert.True(ally.PlayerUpdatesWorker.TryStart(ally, allyUpdates.Rules, null, allyUpdates, out allyUpdates.Generation));
            Game.StepPlayerStats(ally);
            Assert.True(Game.StatsPublishedByFrame(ally));
            Assert.Equal(0, Game.PlayerStat(ally, protection));
            Game.RefreshVaalSouls(caster);
            for (int i = 0; i < budget.BaseCost; i++) Assert.True(caster.VaalSouls.Gain());
            Assert.True(Game.TryBeginSkillCooldownUse(caster, skill.Id, skill.Effect, Game.CooldownForSkill(caster, skill.Id),
                instant: true, () => { }, out var use));
            Assert.True(await Game.CastGemSkill(caster, skill.Id, (100, 100), (100, 100), cooldownUse: use));
            Game.RefreshPeerScale(caster, 100);
            caster.Area.Zones.Update(moved: true);
            Game.WithOwnAuraRecords(ally, () => []);
            Game.StepPlayerStats(ally);
            Assert.Equal(1, Game.CharacterPlayerStats(caster).GetValueOrDefault(protection));
            Assert.Equal(1, Game.PlayerStat(ally, protection));
            var shield = ally.EnergyShield;
            shield.SetMaximum(300, refill: true);
            shield.Damage(150, false, 10);
            shield.Advance(10, 11, key => Game.PlayerStat(ally, key), alive: true);
            Assert.True(shield.Current > 150);
            Assert.True(ally.Area.Buffs.Has(ally.Id, 160, caster.Id));
            var minionStats = Game.MinionAuraStats(ally, minion);
            Assert.Equal(1, minionStats.GetValueOrDefault(protection));
            minion.ShieldPool.SetMaximum(300, refill: true);
            minion.ShieldPool.Damage(150, false, 10);
            minion.ShieldPool.Advance(10, 11, key => minionStats.GetValueOrDefault(key), alive: true);
            Assert.Equal(250u, minion.ShieldPool.Current);

            ally.Walk.Teleport(900, 900, 0);
            caster.Area.Grid.Set(new(ally.Id), (900, 900));
            caster.Area.Zones.Update(moved: true);
            Game.WithOwnAuraRecords(ally, () => []);
            Game.StepPlayerStats(ally);
            Assert.Equal(0, Game.PlayerStat(ally, protection));
            Assert.False(ally.Area.Buffs.Has(ally.Id, 160, caster.Id));
            Assert.Equal(1, Game.MinionAuraStats(ally, minion).GetValueOrDefault(protection));
        }
        finally
        {
            ally.RetirePlayerUpdates();
            caster.Instance.RemovePlayer(ally.Id);
            PlayerInventories.Forget(ally.Character.Name!);
        }
    }

    static Func<uint, int> Stats(bool notDelayed) =>
        name => notDelayed && name == Stat.EnergyShieldRechargeNotDelayedByDamage ? 1 : 0;

    // Vaal Discipline: while the stat holds, damage taken does not hold back recharge (base 2 s delay otherwise).
    [Fact]
    public void DamageDoesNotDelayRechargeWhileTheStatHolds()
    {
        var plain = new EnergyShield();
        var undelayed = new EnergyShield();
        plain.SetMaximum(300, refill: true);
        undelayed.SetMaximum(300, refill: true);
        plain.Damage(150, false, 10);
        undelayed.Damage(150, false, 10);

        plain.Advance(10, 11, Stats(false), true);
        undelayed.Advance(10, 11, Stats(true), true);

        // 300 maximum recharges 2000% a minute: 100 a second.
        Assert.Equal(150u, plain.Current);
        Assert.Equal(250u, undelayed.Current);
        Assert.True(undelayed.Recharging);
        Assert.False(plain.RechargeReady(11, Stats(false)));
        Assert.True(undelayed.RechargeReady(11, Stats(true)));
    }
    [Fact]
    public void Forced_recharge_uses_the_event_time_and_can_be_interrupted()
    {
        var shield = new EnergyShield();
        shield.SetMaximum(300, refill: true);
        shield.Damage(200, false, 10);
        shield.StartRecharge(10.5, Stats(false));
        Assert.True(shield.Recharging);

        shield.Advance(10.25, 11, Stats(false), alive: true);
        Assert.Equal(150u, shield.Current);

        shield.Damage(1, false, 11);
        Assert.False(shield.Recharging);
        shield.Advance(11, 11.5, Stats(false), alive: true);
        Assert.Equal(149u, shield.Current);
    }

    [Fact]
    public void Vaal_Discipline_expiry_clears_both_owned_buffs_and_the_recharge_stat()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var caster = f.Player;
        caster.Connection.PlayerObjectCreated = true;
        f.Equip(("Vaal Discipline", 1));
        var skill = Assert.Single(Game.GemSkillEffects(caster), gem => Program.Skills.EffectId(gem.Effect) == "VaalDiscipline");
        Game.StartTemporaryAuraLocked(caster, skill.Id, "VaalDiscipline", 160, 10, blessing: false);
        Game.WithOwnAuraRecords(caster, () => []);
        const ushort row = 160, tag = (ushort)BuffTag.VaalAura;
        Assert.True(caster.Area.Buffs.Has(caster.Id, row, caster.Id, auraSource: true, tag: tag));
        Assert.True(caster.Area.Buffs.Has(caster.Id, row, caster.Id, tag: tag));
        Assert.Equal(1, Game.CharacterPlayerStats(caster).GetValueOrDefault(Stat.EnergyShieldRechargeNotDelayedByDamage));

        caster.Area.Buffs.Publish();
        Game.StartTemporaryAuraLocked(caster, skill.Id, "VaalDiscipline", row, 10, blessing: false);
        Game.WithOwnAuraRecords(caster, () => []);
        ushort refreshedInstance = caster.Area.Buffs.Next;
        caster.Area.Buffs.Publish();
        Assert.NotNull(caster.Area.Buffs.Find(caster.Id, refreshedInstance)?.U68);

        var state = Game.TemporaryAuraStates.GetOrCreateValue(caster);
        state.Active[skill.Id] = state.Active[skill.Id] with { Until = Game.AreaTime(caster) };
        Game.WithOwnAuraRecords(caster, () => []);
        Assert.Empty(state.Active);
        Assert.False(caster.Area.Buffs.Has(caster.Id, row, caster.Id, auraSource: true, tag: tag));
        Assert.False(caster.Area.Buffs.Has(caster.Id, row, caster.Id, tag: tag));
        Assert.Equal(0, Game.CharacterPlayerStats(caster).GetValueOrDefault(Stat.EnergyShieldRechargeNotDelayedByDamage));
    }
}
