using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SkillTypeEffectTests
{
    [Fact]
    public void Skill_type_passives_scale_trap_crit_guard_recovery_retaliation_speed_and_heralds()
    {
        int TrapCrit(string name, bool allocate)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (allocate && ReferenceEquals(owner, player))
                    values[Stat.TrapCriticalStrikeMultiplierPlus] = 35;
                return values;
            });
            ushort skill = Assert.Single(f.Equip((name, 1)));
            return Game.SupportedCriticalMultiplier(player, skill);
        }

        int trap = TrapCrit("Ice Trap", allocate: false);
        Assert.Equal(trap + 35, TrapCrit("Ice Trap", allocate: true));
        Assert.Equal(TrapCrit("Ice Nova", allocate: false), TrapCrit("Ice Nova", allocate: true));

        // GuardSkillCooldownRecoveryPlusPct syncs StartTriggeredGuard's runtime RecoverySeconds with the skill bar.
        bool GuardReadyAfter(bool allocate, double offsetAfterBuff)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (allocate && ReferenceEquals(owner, player))
                    values[Stat.GuardSkillCooldownRecoveryPlusPct] = 100;
                return values;
            });
            ushort skill = Assert.Single(f.Equip(("Steelskin", 1)));
            var gem = Game.SupportedSkill(player, skill);
            double duration = gem.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration) / 1000d;
            double baseCooldown = Game.SkillsFor(player).CooldownMs(gem.Effect, gem.Level) / 1000d;
            double start = 10;
            Game.StartTriggeredGuard(player, Game.ProjectilesFor(player), skill, start);
            return Game.TriggerGuardReady(player, start + duration + baseCooldown * offsetAfterBuff);
        }

        Assert.False(GuardReadyAfter(allocate: false, offsetAfterBuff: 0.6));
        Assert.True(GuardReadyAfter(allocate: true, offsetAfterBuff: 0.6));

        // RetaliationSkillSpeedPlusPct speeds up both Divine Retribution (spell) and Vengeful Cry (warcry).
        (int SpellMs, double WarcrySeconds) RetaliationTimings(bool allocate)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (allocate && ReferenceEquals(owner, player))
                    values[Stat.RetaliationSkillSpeedPlusPct] = 100;
                return values;
            });
            ushort spellSkill = Assert.Single(f.Equip(("Divine Retribution", 1)));
            var spellGem = Game.SupportedSkill(player, spellSkill);
            int spellMs = Assert.NotNull(Game.CastMs(player, spellGem.Effect, spellSkill));
            ushort crySkill = Assert.Single(f.Equip(("Vengeful Cry", 1)));
            var cryGem = Game.SupportedSkill(player, crySkill);
            double crySeconds = Game.WarcryCastSeconds(player, cryGem.Effect, Game.CharacterPlayerStats(player), cryGem.Stats);
            return (spellMs, crySeconds);
        }

        var baseRetal = RetaliationTimings(allocate: false);
        var fastRetal = RetaliationTimings(allocate: true);
        Assert.True(fastRetal.SpellMs < baseRetal.SpellMs);
        Assert.True(fastRetal.WarcrySeconds < baseRetal.WarcrySeconds);

        // DamagePlusPctWithHeraldSkills scales Herald of Ice's SecondaryPanel tooltip and Herald of Ash's burn DPS.
        (int IcePanelMax, double AshBurnDps) HeraldDamage(bool allocate)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.BaseStrength] = values[Stat.BaseDexterity] = values[Stat.BaseIntelligence] = 500;
                if (allocate) values[Stat.DamagePlusPctWithHeraldSkills] = 50;
                return values;
            });
            ushort iceSkill = Assert.Single(f.Equip(("Herald of Ice", 1)));
            var iceGem = Game.SupportedSkill(player, iceSkill);
            var playerStats = Game.CharacterPlayerStats(player);
            var merged = Game.MergeSkillStats(playerStats, iceGem.Stats);
            var secondaryBase = Game.SkillsFor(player).SecondaryHitDamage(iceGem.Effect, iceGem.Level, iceGem.Quality);
            var panel = Game.SecondaryPanelRows(player, k => merged.GetValueOrDefault(k), playerStats, iceGem, secondaryBase)
                .ToDictionary(r => r.Stat, r => r.Value);
            int iceMax = panel.GetValueOrDefault(Stat.SecondaryMaximumColdDamage);

            ushort ashSkill = Assert.Single(f.Equip(("Herald of Ash", 1)));
            var ashGem = Game.SupportedSkill(player, ashSkill);
            var aura = new AuraCatalog.Aura(ashSkill, ashGem.Effect, "HeraldOfAsh", "Herald of Ash",
                1, 1, 0, 0, Level: ashGem.Level, IsHerald: true);
            var monster = new SpawnedMonster("burn target", new(102, 100, 0), 0, 0, 1000) { Life = 1000 };
            player.Area.Monsters[801] = monster;
            Game.ReservationAshLocked(player, aura, (100, 100), overkill: 1000, now: 1);
            return (iceMax, monster.HeraldBurns.PerSecond(1));
        }

        var baseHerald = HeraldDamage(allocate: false);
        var scaledHerald = HeraldDamage(allocate: true);
        Assert.True(baseHerald.IcePanelMax > 0 && baseHerald.AshBurnDps > 0);
        Assert.Equal((int)Math.Floor(baseHerald.IcePanelMax * 1.5), scaledHerald.IcePanelMax);
        Assert.Equal(baseHerald.AshBurnDps * 1.5, scaledHerald.AshBurnDps, 6);
    }

    [Fact]
    public void A_guaranteed_passive_awards_an_extra_soul_once_on_an_eligible_kill()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.ChanceToGainVaalSoulOnKillPct] = 100;
            return values;
        });
        f.Equip(("Vaal Fireball", 1));
        Game.RefreshVaalSouls(player);
        player.Walk.Teleport(100, 100, 0);

        SpawnedMonster Defeated(uint id)
        {
            var monster = new SpawnedMonster("soul target", new(102, 100, 0), 0, 0, 100)
            {
                Variety = Game.Monsters.Find("Sand Spitter"), Life = 0, Dead = true
            };
            player.Area.Monsters.Add(id, monster);
            return monster;
        }

        var kill = Defeated(720);
        Assert.True(Game.CollectVaalSoulOnKill(player, 720, kill));
        Assert.Equal(2, (int)Assert.Single(player.VaalSouls.Read()).Current);
        Assert.False(Game.CollectVaalSoulOnKill(player, 720, kill));
        player.VaalPreventionUntil = 100;
        Assert.False(Game.CollectVaalSoulOnKill(player, 721, Defeated(721)));
        Assert.Equal(2, (int)Assert.Single(player.VaalSouls.Read()).Current);
    }
}
