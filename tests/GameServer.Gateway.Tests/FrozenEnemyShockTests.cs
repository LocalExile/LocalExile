using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

namespace GameServer.Gateway.Tests;

public sealed class FrozenEnemyShockTests
{
    [Fact]
    public void Call_of_the_brotherhood_shocks_only_already_frozen_targets_with_spells()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        var ring = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Call of the Brotherhood", new Random(11)));
        Game.ItemsOf(conn).Put(PlayerInventories.Ring1, new(100, 0, 0, ring));
        var stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(100, stats[Program.Stats.Key("spell_chance_to_shock_frozen_enemies_%")!.Value]);
        double Shock(bool spell, bool frozen, int lightning, bool immune = false)
        {
            var target = new SpawnedMonster("shock target", new(100, 100, 0), 0, 0, 100)
            {
                Mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
                    { ["cannot_be_shocked"] = immune ? 1 : 0 }))
            };
            if (frozen) target.Ailments.Freeze(3, 0, _ => 0, _ => 0);
            Game.AilmentsOnHitLocked(conn, 10, target, DamagePacket.Of(DamageType.Lightning, lightning, lightning), spell,
                critical: false, 100, stats, null, 1, new Random(11));
            return target.Ailments.Magnitude(Kind.Shock, 1);
        }
        Assert.Equal(50, Shock(spell: true, frozen: true, 100));
        Assert.Equal(0, Shock(spell: true, frozen: false, 100));
        Assert.Equal(0, Shock(spell: false, frozen: true, 100));
        Assert.Equal(0, Shock(spell: true, frozen: true, 100, immune: true));
    }
}
