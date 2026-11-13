using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Blood_dance_target_life_uses_pre_hit_state_and_frozen_charges(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        conn.PlayerLife = conn.PlayerMaxLife;
        var boots = wear("The Blood Dance", PlayerInventories.Boots);
        var actual = Game.CharacterPlayerStats(conn);
        uint input = Program.Stats.Key(Game.DamageVsLowLifePerFrenzy)!.Value;
        int coefficient = actual.GetValueOrDefault(input); Assert.True(coefficient > 0);
        var target = new SpawnedMonster("Blood Dance target", new(100, 100, 1), 100, 8, 1000)
            { Hostile = true, Life = 1000 };
        const uint targetId = 0x504BD; conn.Monsters[targetId] = target;
        foreach (uint life in new uint[] { 0, 499, 500, 501, 1000 })
        {
            target.Life = life;
            Assert.Equal(life is > 0 and <= 500, Game.EnemyOnLowLife(target));
        }
        var friendly = new SpawnedMonster("Friendly low Life", new(0, 0, 1), 0, 0, 1000)
            { Hostile = false, Life = 500 };
        Assert.False(Game.EnemyOnLowLife(friendly));
        var wide = new SpawnedMonster("Wide Life target", new(0, 0, 1), 0, 0, uint.MaxValue)
            { Hostile = true, Life = uint.MaxValue / 2 + 1 };
        Assert.False(Game.EnemyOnLowLife(wide)); wide.Life--; Assert.True(Game.EnemyOnLowLife(wide));
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50);
        uint frenzy = Program.Stats.Key("current_frenzy_charges")!.Value;
        var stats = new Dictionary<uint, int> { [input] = coefficient,
            [Program.Stats.Key("damage_+%")!.Value] = 25 };
        foreach (int count in new[] { 0, 1, 3 })
        foreach (var (attack, secondary) in new[] { (true, false), (false, false), (false, true) })
        foreach (bool low in new[] { false, true })
        {
            stats[frenzy] = count;
            var source = new SpiderDamageSource(raw, conversion, stats, stats, attack, attack, true, true,
                [], new HashSet<int>(), 1, Secondary: secondary, Volatile: false);
            var profile = Game.SpiderDamageProfile(source, 0, false, lowLife: low);
            double factor = 1.25 + (low ? count * coefficient / 100d : 0);
            Assert.Equal(200 * factor, profile.Hit.MinTotal, 6);
            Assert.InRange(profile.Ignite.Min, 150 * factor * .9 - 1d / 60, 150 * factor * .9 + 1d / 60);
            if (attack)
                Assert.InRange(profile.Bleed.Min, 50 * factor * .7 - 1d / 60, 50 * factor * .7 + 1d / 60);
            var shot = new ProjectileOffense(DamagePacket.Empty, !attack, 0, 0, 100, "Blood Dance target")
                { Spider = source, SpiderArea = source };
            Assert.Equal(profile.Hit.MinTotal, shot.Against(false, lowLife: low).Hit.MinTotal, 6);
            Assert.Equal(profile.Hit.MinTotal, shot.Against(false, lowLife: low).AreaHit!.MinTotal, 6);
            foreach (var (hitMore, ailmentMore) in new[] { (1.6, 1d), (.6, .6), (.96, .6) })
            {
                var repeated = shot with { HitProfileMore = hitMore, AilmentProfileMore = ailmentMore };
                var selected = repeated.Against(false, lowLife: low);
                Assert.Equal(profile.Hit.MinTotal * hitMore, selected.Hit.MinTotal, 6);
                Assert.Equal(profile.Hit.MinTotal * hitMore, selected.AreaHit!.MinTotal, 6);
                Assert.Equal(profile.Ignite.Min * ailmentMore, selected.Ignite.Min, 6);
                Assert.Equal(profile.Poison.Min * ailmentMore, selected.Poison.Min, 6);
                Assert.Equal(selected.Hit.MinTotal, selected.Against(false, lowLife: low).Hit.MinTotal, 6);
                var alternate = repeated with { Spider = null, SpiderArea = null,
                    VsBlinded = new(profile.Hit, profile.Hit, profile.Ignite, profile.Bleed, profile.Poison) };
                Assert.Equal(profile.Hit.MinTotal * hitMore, alternate.Against(true).Hit.MinTotal, 6);
                Assert.Equal(profile.Ignite.Min * ailmentMore, alternate.Against(true).Ignite.Min, 6);
            }
        }
        stats[frenzy] = 3;
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var inputs = new Game.SupportedHitInputs(raw, Get, conversion, true, true, false, true,
            [], [], new(SkillHitKind.Attack, DamagePacket.Empty), new(stats)) { FrozenStats = new Dictionary<uint, int>(stats) };
        var captured = Assert.IsType<SpiderDamageSource>(Game.CaptureSpiderDamage(conn, inputs, 1));
        stats[frenzy] = 0; stats[input] = 0;
        Assert.Equal(200 * (1.25 + 3 * coefficient / 100d),
            Game.SpiderDamageProfile(captured, 0, false, lowLife: true).Hit.MinTotal, 6);
        var snapshot = new Dictionary<uint, int>(captured.Stats);
        var weapon = Game.WeaponFor(conn);
        foreach (uint life in new uint[] { 499, 500, 501 })
        {
            target.Life = life;
            var selected = Assert.IsType<Game.DefaultMeleePrehitProfile>(Game.DefaultMeleePrehitProfileLocked(
                conn, targetId, target, snapshot, weapon, conversion, null, Game.ResourceEffectsTime(conn)));
            var projected = new Dictionary<uint, int>(snapshot)
                { [Program.Stats.Key("damage_+%")!.Value] = 25 + (life <= 500 ? 3 * coefficient : 0) };
            Assert.Equal(Game.AttackHitConverted(weapon, projected, weapon.Melee, conversion).MinTotal,
                selected.Hit.MinTotal, 6);
            Assert.Equal(25, snapshot[Program.Stats.Key("damage_+%")!.Value]);
        }
        conn.Monsters.Remove(targetId);
        Assert.Null(Game.DefaultMeleePrehitProfileLocked(conn, targetId, target, snapshot, weapon, conversion, null,
            Game.ResourceEffectsTime(conn)));
        Game.ItemsOf(conn).Remove(PlayerInventories.Boots, boots.Id);
        Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(input));
    }
}
