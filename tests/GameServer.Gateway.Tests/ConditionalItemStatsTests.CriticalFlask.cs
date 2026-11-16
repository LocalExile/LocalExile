using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Actual_coralito_suppresses_extra_critical_hit_damage(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var flask = new UniqueCatalog(Program.Items).Create("Coralito's Signature", new Random(504))!;
        var local = ItemModStats.Local(flask, Program.Items.Mods);
        Assert.Equal(1, local["local_no_critical_strike_multiplier_during_flask_effect"]);
        var utility = Program.Items.UtilityFlaskOf(Program.Items.ByHash(flask.TypeHash)!)!;
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot().ToArray();
        var overrides = conn.PlayerStats.ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        conn.PlayerStats.Clear(); conn.Experience = CharacterStats.ExperienceFor(100); conn.PlayerLife = 1000;
        Assert.Empty(conn.UtilityFlaskEffects.Stats());
        try
        {
            wear("Astramentis", 5);
            var weapon = wear("Redbeak", PlayerInventories.Weapon1);
            var gems = new[] { "SkillGemDoubleStrike", "SkillGemFireball", "SupportGemIncreasedCriticalDamage" }
                .Select(name => new ItemSocket(SocketColour.White,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!))).ToArray();
            inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with { Sockets = gems, Links = [3] });
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            var baseline = Game.CharacterPlayerStats(conn);
            var attack = Game.GemSkillEffects(conn).Single(s => Game.SkillsFor(conn).EffectId(s.Effect) == "DoubleStrike");
            var spell = Game.GemSkillEffects(conn).Single(s => Game.SkillsFor(conn).EffectId(s.Effect) == "Fireball");
            int attackBefore = Game.SupportedCriticalMultiplier(conn, attack.Id);
            int spellBefore = Game.SupportedCriticalMultiplier(conn, spell.Id);
            Assert.True(attackBefore > 150); Assert.True(spellBefore > 150);
            var target = new SpawnedMonster("critical flask target", new(0, 0, 8), 10, 2, 10000);
            var ailmentInput = new Dictionary<uint, int>(baseline)
            {
                [Key("critical_ailment_dot_multiplier_+")] = 50,
            };
            var raw = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
            var ailmentsBefore = AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true), raw, new(),
                n => Program.Stats.Key(n) is uint k ? ailmentInput.GetValueOrDefault(k) : 0);
            foreach (int increasedEffect in new[] { 0, 200, -90 })
            {
                var effect = Game.ItemUtilityFlaskFor(utility, flask, local,
                    n => n == "flask_effect_+%" ? increasedEffect : 0, 100);
                Assert.Equal(1, Assert.Single(effect.Stats, s => s.Stat == "no_critical_strike_multiplier").Value);
                var active = new UtilityFlasks.Active(504, 2, "Coralito", utility, 1, null,
                    effect.Stats, 10, 10 + effect.Seconds);
                conn.UtilityFlaskEffects.Start(active);
                conn.Pools = Game.PoolsFor(conn, 100);
                var frozen = Game.CharacterPlayerStats(conn);
                Assert.Equal(1, frozen.GetValueOrDefault(Key("no_critical_strike_multiplier")));
                Assert.Equal(100, conn.Pools.CriticalStrikeMultiplier);
                Assert.Equal(100, conn.Pools.SpellCriticalStrikeMultiplier);
                Assert.Equal(100, Game.AttackCriticalMultiplier(conn, frozen));
                Assert.Equal(100, Game.SupportedCriticalMultiplier(conn, attack.Id));
                Assert.Equal(100, Game.SupportedCriticalMultiplier(conn, spell.Id));
                Assert.Equal(100, Game.CriticalMultiplierOn(target, 400, frozen));
                Assert.Equal(400, Game.CriticalMultiplierOn(target, 400, baseline));
                Assert.Equal(100, Game.CriticalMultiplierOn(target, 400,
                    noExtra: Game.CriticalHitsDealNoExtraDamage(frozen)));
                Assert.Equal(100, OrdinaryRangedShot.Capture(Game.WeaponFor(conn), frozen, [], Program.Stats,
                    1000, 10000, 400).CriticalMultiplier);
                Assert.Equal(100, Game.CriticalHitMultiplier(400, baseline,
                    new Dictionary<uint, int> { [Key("no_critical_strike_multiplier")] = 1 }));
                Assert.Equal(100, DamageModel.CriticalDamage(100, Game.CriticalMultiplierOn(target, 400, frozen)));
                Assert.True(Game.SupportedCriticalChance(conn, spell.Id) > 0);
                ailmentInput[Key("no_critical_strike_multiplier")] = 1;
                Assert.Equal(ailmentsBefore, AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true), raw, new(),
                    n => Program.Stats.Key(n) is uint k ? ailmentInput.GetValueOrDefault(k) : 0));
                Assert.Empty(conn.UtilityFlaskEffects.End(active.Ends - .001, false, out _));
                Assert.Single(conn.UtilityFlaskEffects.End(active.Ends, false, out _));
                conn.Pools = Game.PoolsFor(conn, 100);
                var expired = Game.CharacterPlayerStats(conn);
                Assert.Equal(0, expired.GetValueOrDefault(Key("no_critical_strike_multiplier")));
                Assert.Equal(attackBefore, Game.SupportedCriticalMultiplier(conn, attack.Id));
                Assert.Equal(spellBefore, Game.SupportedCriticalMultiplier(conn, spell.Id));
                Assert.Equal(100, Game.CriticalMultiplierOn(target, 400, frozen));
                conn.UtilityFlaskEffects.Start(active);
                Assert.Single(conn.UtilityFlaskEffects.End(10, false, out _, a => UtilityFlasks.LeftItsCell(a, [])));
                conn.UtilityFlaskEffects.Start(active);
                Assert.Single(conn.UtilityFlaskEffects.End(10, true, out _));
            }
        }
        finally
        {
            conn.UtilityFlaskEffects.End(0, true, out _);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.PlayerStats.Clear(); foreach (var p in overrides) conn.PlayerStats[p.Key] = p.Value;
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife; Game.CharacterPlayerStats(conn);
        }
    }
}
