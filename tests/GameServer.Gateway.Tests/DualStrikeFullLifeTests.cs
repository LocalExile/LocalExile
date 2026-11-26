using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DualStrikeFullLifeTests
{
    [Fact]
    public async Task Dual_Strike_scales_hits_ailments_and_critical_chance_only_against_full_life()
    {
        var full = await Strike("Dual Strike", fullLife: true);
        var injured = await Strike("Dual Strike", fullLife: false);
        Assert.InRange((double)full.Damage / injured.Damage, 1.58, 1.62);
        Assert.InRange(full.Poison / injured.Poison, 1.58, 1.62);

        var fullCritical = await Strike("Dual Strike", fullLife: true, nearCritical: true);
        var injuredCritical = await Strike("Dual Strike", fullLife: false, nearCritical: true);
        Assert.InRange((double)fullCritical.Damage / full.Damage, 1.48, 1.52);
        Assert.InRange((double)injuredCritical.Damage / injured.Damage, 0.98, 1.02);

        var variantFull = await Strike("Dual Strike of Ambidexterity", fullLife: true, nearCritical: true);
        var variantInjured = await Strike("Dual Strike of Ambidexterity", fullLife: false);
        Assert.InRange((double)variantFull.Damage / variantInjured.Damage, 0.98, 1.02);
        Assert.InRange(variantFull.Poison / variantInjured.Poison, 0.98, 1.02);
    }

    static async Task<(uint Damage, double Poison)> Strike(string gemName, bool fullLife, bool nearCritical = false)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "dual-strike", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));

        var inventory = Game.ItemsOf(player);
        var host = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var sword = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!);
        inventory.Change(PlayerInventories.Weapon1, host.Id, _ => sword);
        inventory.Add(PlayerInventories.Offhand1, 0, 0, sword);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalAlwaysHit] = 1;
            values[Stat.GlobalMinimumAddedPhysicalDamage] = 1000;
            values[Stat.GlobalMaximumAddedPhysicalDamage] = 1000;
            values[Stat.BaseChanceToPoisonOnHitPct] = 100;
            return values;
        });
        ushort skill = Assert.Single(fixture.Equip((gemName, 1)));
        var gem = Game.GemSkillEffects(player).Single(g => g.Id == skill);
        var target = new SpawnedMonster("dual strike witness", new(108, 100, 0), 0, 0, 100_000)
        {
            ObjectId = 901, Hostile = true, NoRewards = true,
            Life = fullLife ? 100_000u : 90_000u,
        };
        player.Area.Monsters[target.ObjectId] = target;
        player.Area.Grid.Set(new(target.ObjectId), (108, 100));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            int chance = Game.SupportedCriticalChance(player, skill, AttackHand.Both);
            Assert.InRange(chance, 1, 4900);
            var criticalRoll = new SkillCriticalRoll(0);
            Assert.True(criticalRoll.Check(10_000, new FixedRoll(nearCritical ? chance + 1 : 9999)));
            uint before = target.Life;
            await Game.AreaSkillHit(player, skill, gem, (100, 100), (108, 100), immediate: true,
                sequence: 0x8002, criticalRoll: criticalRoll, targetId: target.ObjectId, singleStrikeTarget: target.ObjectId);
            Assert.True(target.Life < before);
            double poison = target.Poisons.TotalDps(clock.At);
            Assert.True(poison > 0);
            return (before - target.Life, poison);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class FixedRoll(int roll) : Random
    {
        public override int Next(int maxValue) => roll;
    }
}
