using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SublimeVisionVitalityTests
{
    [Fact]
    public void Vitality_jewel_regenerates_after_accepted_hits_and_refreshes_without_stacking()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        f.Equip(("Vitality", 1));
        var catalog = Game.ItemsFor(conn);
        var unique = Game.UniquesFor(conn);
        var jewel = Enumerable.Range(0, 128).Select(seed => unique.Create("Sublime Vision", new Random(seed)))
            .OfType<Item>().First(i => i.Mods.Any(mod => catalog.Mods.ByHash(mod.Hash)?.Id == "SublimeVisionVitality"));
        conn.Passives = [Game.PassivesFor(conn).JewelNodeForSlot(1)!.Value];
        Game.ItemsOf(conn).Add(PlayerInventories.PassiveJewels, 1, 0, jewel);
        var recovery = Game.ResourceEffectsOf(conn).VitalityHitRecovery;

        Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 1, false, false, false, out _));
        Assert.Equal(0, recovery.Rate(0));
        CharacterSaves.SetAuraActive(conn.Character!, "Vitality", true);
        Game.ReconcileOwnAuras(conn);
        Assert.Contains(conn.Auras.Values, aura => aura.EffectId == "Vitality");

        Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 1, false, false, false, out _));
        double rate = conn.PlayerMaxLife * 0.15;
        double first = recovery.Until - 1;
        Assert.True(rate > 0);
        Assert.Equal(rate, recovery.Rate(first + .25), 6);
        double refresh = first + .5;
        Game.VitalityHitRecoveryLocked(conn, refresh, Game.CharacterPlayerStats(conn));
        Assert.Equal(refresh + 1, recovery.Until, 6);
        Assert.Equal(1.5 * rate, recovery.Take(refresh + 1), 6);

        CharacterSaves.SetAuraActive(conn.Character!, "Vitality", false);
        f.Equip();
        Game.ReconcileOwnAuras(conn, validateSkills: true);
        Assert.DoesNotContain(conn.Auras.Values, aura => aura.EffectId == "Vitality");
        Game.VitalityHitRecoveryLocked(conn, refresh + 2, Game.CharacterPlayerStats(conn));
        Assert.Equal(refresh + 1, recovery.Until, 6);
    }
}
