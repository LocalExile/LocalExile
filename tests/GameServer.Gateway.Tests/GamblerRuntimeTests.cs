using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GamblerRuntimeTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Theory]
    [InlineData(false)]
    public async Task Live_projectile_contacts_apply_less_and_triple_once_without_multiplying_stored_impale(bool spell)
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(40, 10);
        f.Conn.Monsters.Add(42, target);
        if (!spell) target.Impale.Apply(100); // Stored reflection is 10 per hit.
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 20, 20), spell, int.MaxValue, 0, 150, "gambler");
        f.Release(f.Shot(new("Metadata/Projectiles/DefaultArrow") { Speed = 100, Range = 100, Radius = 1 }, offense));
        // Both inputs are live contact policies, not baked into the launched packet.
        f.Conn.PlayerStats[Key("marauder_hidden_ascendancy_damage_+%_final")] = -30;
        f.Conn.PlayerStats[Key("base_chance_to_deal_triple_damage_%")] = 100;
        f.Advance(1);
        Assert.Equal(spell ? 58u : 48u, target.Life);
    }

    sealed class SequenceRandom(params int[] values) : Random
    {
        readonly Queue<int> remaining = new(values);
        public override int Next(int minValue, int maxValue) => remaining.Dequeue();
    }
}
