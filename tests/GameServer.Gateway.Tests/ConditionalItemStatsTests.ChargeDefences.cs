using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_charge_defence_coefficients_reach_hit_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        conn.PlayerLife = conn.PlayerMaxLife;
        var inventory = Game.ItemsOf(conn); var charges = Game.ChargesOf(conn);
        var attributes = wear("Astramentis", 5);
        var weapon = wear("Hopeshredder", PlayerInventories.Weapon1);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int coefficient = Get(ItemChargeDefences.ElementalAvoidPerFrenzy); Assert.True(coefficient > 0);
        void Count(ChargeType type, int count)
        {
            charges.Admit(Game.ResourceEffectsTime(conn), [new ChargeEvent.Set(type, count)], Get,
                () => Game.WeaponProfileFor(conn), true, new Random(1));
            stats = Game.CharacterPlayerStats(conn);
        }
        foreach (int count in new[] { 0, 1, 3, 0 })
        {
            Count(ChargeType.Frenzy, count);
            foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
            {
                Assert.Equal(count * coefficient, PlayerDamageTaken.AvoidPct(type, Get));
                if (count > 0)
                {
                    Assert.True(PlayerDamageTaken.Avoids(type, Get, new PhasingRoll(count * coefficient - 1)));
                    Assert.False(PlayerDamageTaken.Avoids(type, Get, new PhasingRoll(count * coefficient)));
                }
            }
            Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Physical, Get));
            Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Chaos, Get));
        }
        Count(ChargeType.Frenzy, 3);
        var conversion = wear("Olesya's Delight", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("current_frenzy_charges"));
        Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Fire, Get));
        inventory.Remove(PlayerInventories.Belt, conversion.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(3 * coefficient, PlayerDamageTaken.AvoidPct(DamageType.Fire, Get));
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Fire, Get));

        weapon = wear("Nebuloch", PlayerInventories.Weapon1);
        stats = Game.CharacterPlayerStats(conn);
        coefficient = Get(ItemChargeDefences.ElementalHitTakenPerEndurance); Assert.True(coefficient < 0);
        Count(ChargeType.Endurance, 0);
        int ordinary = PlayerDamageTaken.IncreasedPct(DamageType.Fire, false, Get);
        double carry = 0; int dot = IncomingResourceDefences.TakenOverTime(1000, Get, ref carry);
        foreach (int count in new[] { 1, 3, 0 })
        {
            Count(ChargeType.Endurance, count);
            foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
                Assert.Equal(ordinary + count * coefficient, PlayerDamageTaken.IncreasedPct(type, false, Get));
            Assert.Equal(0, PlayerDamageTaken.IncreasedPct(DamageType.Physical, false, Get));
            Assert.Equal(0, PlayerDamageTaken.IncreasedPct(DamageType.Chaos, false, Get));
            carry = 0; Assert.Equal(dot, IncomingResourceDefences.TakenOverTime(1000, Get, ref carry));
        }
        Count(ChargeType.Endurance, 3);
        conversion = wear("Arn's Anguish", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("current_endurance_charges"));
        Assert.Equal(0, Get(ItemChargeDefences.ElementalHitTaken));
        inventory.Remove(PlayerInventories.Belt, conversion.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(3 * coefficient, Get(ItemChargeDefences.ElementalHitTaken));
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id); inventory.Remove(5, attributes.Id);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get(ItemChargeDefences.ElementalHitTaken));
        Game.ChargesOnDeath(conn);
    }
}
