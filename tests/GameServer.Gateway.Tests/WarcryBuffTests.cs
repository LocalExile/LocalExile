using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryBuffTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 1;
        public double Now(Player _) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            Time += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Echoes_of_Creation_adds_a_stored_use_only_to_its_socketed_warcry()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort unlinked = Assert.Single(fixture.Equip(("Enduring Cry", 1)));
        var helmet = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Echoes of Creation", new Random(1)));
        var seismic = Program.Items.FindGem("Seismic Cry")!.Value;
        Game.ItemsOf(player).Add(PlayerInventories.Helmet, 0, 0, helmet with
        {
            Sockets = [new(SocketColour.Red, ItemCatalog.Create(seismic.Type))],
            Links = [1]
        });
        ushort skill = Game.GemSkillEffects(player).Single(g => g.Id != unlinked).Id;
        var supported = Game.SupportedSkill(player, skill);
        Assert.Contains(supported.Supports, support => support.Effect.Id == "SupportWarcryCooldownCount");
        Assert.Equal(1, supported.Stats[Stat.BaseAddedCooldownCount]);
        Assert.Equal(0, Game.SupportedSkill(player, unlinked).Stats.GetValueOrDefault(Stat.BaseAddedCooldownCount));
        Assert.Equal(1, Game.CooldownForSkill(player, unlinked)!.Value.Uses);
        int nativeUses = Game.SkillsFor(player).CooldownUses(supported.Effect, supported.Level);
        var cooldown = Game.CooldownForSkill(player, skill);
        Assert.Equal(2, cooldown?.Uses ?? nativeUses);
        var displayed = Game.PlayerSkills(player).Single(g => g.Id == skill);
        Assert.Contains((Stat.VirtualTotalCooldownCount, 2), displayed.Stats!);

        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            bool Admit() => Game.TryBeginSkillCooldownUse(player, skill, supported.Effect, cooldown,
                false, () => { }, out _);

            Assert.True(Admit());
            Assert.True(Admit());
            uint mana = player.PlayerMana;
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);
            var state = Game.SingleUseState(player, supported.Effect, skill)!;
            Assert.Equal(2, state.SpentUses);

            double recharge = cooldown!.Value.BaseMilliseconds / (1000 * cooldown.Value.RecoveryRate);
            clock.Time += recharge - 0.01;
            Assert.False(Admit());
            clock.Time += 0.01;
            Game.RefreshSingleUseCooldowns(player);
            Assert.Equal(1, state.SpentUses);
            Assert.True(Admit());
            Assert.False(Admit());
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public void Seismic_Cry_grants_its_row_stats_with_the_rows_own_value_count()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        ushort id = Assert.Single(f.Equip(("Seismic Cry", 1)));
        var gem = Game.SupportedSkill(player, id);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        var playerStats = new Dictionary<uint, int> { [Stat.WarcriesHaveInfinitePower] = 1 };

        // Row 1544 (seismic_cry) carries exactly two GrantedStats, in this order, at 25 power (five whole
        // 5-power steps), using the equipped gem's armour and stun-threshold values.
        var stats = Game.WarcryBuffStats("SeismicCry", 25, 1.0, Own);
        Assert.NotNull(stats);
        Assert.Equal(2, stats.Length);
        Assert.Equal(Stat.ArmourPlusPctFinalFromSeismicCryBuff, stats[0].Key);
        Assert.Equal(5 * Own(Stat.SeismicCryArmourPlusPctFinalPer5PowerUpToCap), stats[0].Value);
        Assert.Equal(Stat.StunThresholdPlusPct, stats[1].Key);
        Assert.Equal(5 * Own(Stat.SeismicCryStunThresholdPlusPctPer5PowerUpToCap), stats[1].Value);

        var previous = Game.AreaUpdateContext.Value;
        var clock = new Clock();
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            using var modifiers = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (owner == player) values[Stat.BasePhysicalDamageReductionRating] = 100;
                return values;
            });
            var body = new SpawnedMonster("ally", new(100, 100, 0), 10, 3, 100) { Hostile = false };
            player.Area.Monsters[900] = body;
            var ally = new GolemState.Golem(900, body, Game.GolemData.Find("SummonStoneGolem")!, 1);
            var before = Game.BuildPoolsFor(player, 100);
            Game.CharacterPlayerStats(player);
            Game.ApplyWarcryBuffLocked(player, "SeismicCry", gem.Stats, playerStats, (100, 100), 60, clock.Time, 4, 1);
            Assert.True(stats[0].Value > 0);
            Assert.Equal(stats[0].Value, Game.MinionAuraStats(player, ally)[stats[0].Key]);
            Assert.True(player.Area.Buffs.Has(player.Id, 1544, player.Id));
            Assert.True(player.Pools.Armour > before.Armour);

            id = Assert.Single(f.Equip(("Ancestral Cry", 1)));
            var ancestral = Game.SupportedSkill(player, id);
            clock.Time = 2;
            Game.ApplyWarcryBuffLocked(player, "AncestralCry", ancestral.Stats, playerStats, (100, 100), 60, clock.Time, 5, 1);
            Assert.Equal(before.FireMaxResistance + 3, player.Pools.FireMaxResistance);
            Assert.True(player.Pools.Armour > before.Armour);

            clock.Time = 5;
            Game.ExpireWarcryBuffs(player);
            Assert.Equal(before.Armour, player.Pools.Armour);
            Assert.Equal(0, Game.MinionAuraStats(player, ally).GetValueOrDefault(stats[0].Key));
            Assert.Equal(before.FireMaxResistance + 3, player.Pools.FireMaxResistance);
            clock.Time = 7;
            Game.ExpireWarcryBuffs(player);
            Assert.Equal(before.FireMaxResistance, player.Pools.FireMaxResistance);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public void Generals_Cry_grants_no_buff()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var playerStats = new Dictionary<uint, int> { [Stat.WarcriesHaveInfinitePower] = 1 };

        Assert.Null(Game.WarcryBuffRow("GeneralsCry"));
        Assert.Null(Game.WarcryBuffStats("GeneralsCry", 25, 1.0, _ => 0));

        var before = Game.CharacterPlayerStats(player);
        Game.ApplyWarcryBuffLocked(player, "GeneralsCry", new Dictionary<uint, int>(), playerStats, (100, 100), 60, 1, 4.0, 1.0);
        var after = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.DamagePlusPct), after.GetValueOrDefault(Stat.DamagePlusPct));
        Assert.Equal(before.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute), after.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute));
    }

    [Fact]
    public void Vengeful_Cry_spends_a_savage_hit_window_and_grants_a_timed_rage_buff()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Vengeful Cry", 1)));
        var gem = Game.SupportedSkill(player, skill);
        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var attacker = new SpawnedMonster("warcry witness", new(106, 100, 0), 0, 0, 10000)
            {
                ObjectId = 951, Hostile = true, NoRewards = true, Rarity = MonsterRarity.Unique, Level = 30
            };
            player.Area.Monsters[attacker.ObjectId] = attacker;
            void TakeHit(int percent)
            {
                player.PlayerLife = player.PlayerMaxLife;
                int damage = (int)(player.PlayerMaxLife * percent / 100);
                var attack = new MonsterAttack(0, 1, 20, DamagePacket.Of(DamageType.Chaos, damage, damage)) { Secondary = true };
                Assert.NotNull(Game.CommitMonsterHitLocked(player, attacker.ObjectId, attacker, attack,
                    clock.Time, (100, 100), (106, 100), checkRange: false));
            }
            bool Admit() => Game.TryBeginSkillCooldownUse(player, skill, gem.Effect, Game.CooldownForSkill(player, skill),
                false, () => { }, out _);

            uint mana = player.PlayerMana;
            Assert.False(Admit());
            TakeHit(5);
            Assert.False(Admit());
            TakeHit(25);
            int window = gem.Stats.GetValueOrDefault(Stat.RetaliationBaseUseWindowDurationMs);
            Assert.Equal(2000, window);
            clock.Time += window / 1000d;
            Assert.True(player.RecentEvents.Happened(StatConditions.TakenSavageHit, clock.Time));
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);
            TakeHit(25);
            Assert.True(Admit());
            Assert.True(player.PlayerMana < mana);
            Assert.False(Game.TryBeginSkillCooldownUse(player, skill, gem.Effect, null, false, () => { }, out _, reserve: false));

            var before = Game.CharacterPlayerStats(player);
            var power = new Dictionary<uint, int> { [Stat.WarcriesHaveInfinitePower] = 1 };
            Game.ApplyWarcryBuffLocked(player, "VengefulCry", gem.Stats, power, (100, 100), 60, clock.Time, 4, 1);
            Assert.True(player.Area.Buffs.Has(player.Id, Game.VengefulCryBuffRow, player.Id));
            var buffed = Game.CharacterPlayerStats(player);
            Assert.Equal(before.GetValueOrDefault(Stat.MaximumRage) + 25, buffed.GetValueOrDefault(Stat.MaximumRage));
            Assert.Equal(before.GetValueOrDefault(Stat.BaseRageRegenerationPerMinute) + 900,
                buffed.GetValueOrDefault(Stat.BaseRageRegenerationPerMinute));

            clock.Time += 4;
            Game.ExpireWarcryBuffs(player);
            Assert.False(player.Area.Buffs.Has(player.Id, Game.VengefulCryBuffRow, player.Id));
            var expired = Game.CharacterPlayerStats(player);
            Assert.Equal(before.GetValueOrDefault(Stat.MaximumRage), expired.GetValueOrDefault(Stat.MaximumRage));
            Assert.Equal(before.GetValueOrDefault(Stat.BaseRageRegenerationPerMinute), expired.GetValueOrDefault(Stat.BaseRageRegenerationPerMinute));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
