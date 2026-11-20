using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EssenceAuraTests
{
    [Fact]
    public void Released_essences_report_their_pending_skills_without_misreporting_implemented_behaviours()
    {
        int[] Carriers(string mod) => [.. MapEssenceCatalog.All.Where(e => e.MonsterMods.Contains(Program.Items.Mods.ById(mod)!.Row)).Select(e => e.Row)];
        foreach (string id in new[] { "EssenceDaemonMeteorFirestorm", "EssenceHorrorCharges1", "EssenceInsectSummonedOnDeath" })
        {
            var carriers = Carriers(id);
            Assert.NotEmpty(carriers);
            Assert.Contains(id, MapEssenceCatalog.PendingEffects(carriers));
        }
        foreach (string id in new[] { Game.EssenceLifeDegen, Game.EssenceKaruiSummonMod, "EssenceEnrageOnLowLife", "DecayOnHitEssenceMonster1" })
        {
            var carriers = Carriers(id);
            Assert.NotEmpty(carriers);
            Assert.DoesNotContain(id, MapEssenceCatalog.PendingEffects(carriers));
        }
        var rage = Carriers("EssenceDaemonMeteorFirestorm");
        Assert.Equal(MapEssenceCatalog.PendingEffects(rage), MapEssenceCatalog.PendingEffects(rage.Concat(rage)));
    }

    [Fact]
    public void Essence_degen_uses_recipient_life_resistance_and_ES_without_stacking_and_cleans_up()
    {
        var conn = new ClientState(new(1, "essence-aura", 300, 300, 100, 100, null, []), 1, 1)
            { Pools = new(70, 10000, 100, 0, 0), PlayerLife = 10000 };
        conn.Walk.Teleport(300, 300, 0);
        var mod = Program.Items.Mods.ById(Game.EssenceLifeDegen)!;
        var set = Game.MonsterModRolls.Value.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70);
        SpawnedMonster Source(int x) => new("envy", new(x, 300, 30), 60, 8, 100) { Mods = set };
        var first = Source(350);
        var second = Source(320);
        conn.Monsters[42] = first;
        conn.Monsters[43] = second;
        var stats = new Dictionary<uint, int>();
        int Step(double from, double until) => Game.AdvanceEssenceAuraLocked(conn, from, until, stats, false, double.NegativeInfinity);
        try
        {
            Game.TickEssenceAurasLocked(conn, 0);
            Assert.Equal(400, Step(0, 1)); // radius 50 inclusive, duplicate auras do not stack
            Assert.Equal(0, Step(0, 1));
            Assert.Single(conn.Minions.OutputQueue, p => p.Log == "Essence degeneration aura");
            conn.EnergyShield.SetMaximum(1000, refill: true);
            Assert.Equal(400, Step(1, 2));
            Assert.Equal(1000u, conn.EnergyShield.Current);
            stats[Program.Stats.Key("chaos_damage_does_not_bypass_energy_shield")!.Value] = 1;
            Assert.Equal(0, Step(2, 3));
            Assert.Equal(600u, conn.EnergyShield.Current);
            stats.Clear();
            conn.Pools = conn.Pools with { ChaosResistance = 50 };
            Assert.Equal(200, Step(3, 4));
            conn.Pools = conn.Pools with { ChaosResistance = 0 };
            conn.Immune = true;
            Assert.Equal(0, Step(4, 5));
            conn.Immune = false;
            Assert.Equal(0, Game.AdvanceEssenceAuraLocked(conn, 5, 6, stats, true, double.NegativeInfinity));
            first.Dead = true;
            Assert.Equal(400, Step(6, 7)); // the remaining source keeps the same rate
            second.Walk.Teleport(351, 300, 6);
            Assert.Equal(0, Step(7, 8));
            Assert.Contains(conn.Minions.OutputQueue, p => p.Log == "Essence degeneration aura ends");
            second.Walk.Teleport(320, 300, 7);
            stats[Program.Stats.Key("keystone_lethe_shade")!.Value] = 1;
            Assert.Equal(100, Step(8, 8.5));
            Assert.Equal(200, Step(8.5, 9)); // one-second boundary no longer has Lethe Shade
            stats.Clear();
            Game.ClearEssenceAuraExposure(conn);
            Assert.Equal(0, Step(9, 10));

            var minion = new SpawnedMonster("ally", new(300, 300, 30), 60, 8, 10000) { Hostile = false };
            var definition = new GolemCatalog.Definition(-1, "test", "Skeleton", "test", 0, [], false, false, false);
            var g = new GolemState.Golem(44, minion, definition, 0);
            g.ShieldPool.SetMaximum(1000, refill: true);
            conn.Summons.Fighters[44] = g;
            conn.Monsters[44] = minion;
            Game.TickEssenceAurasLocked(conn, 10);
            Game.TickEssenceAurasLocked(conn, 11);
            Assert.Equal(9600u, minion.Life);
            Assert.Equal(1000u, g.ShieldPool.Current);
            Assert.NotEmpty(minion.Ailments.Inflict(DamagePacket.Of(DamageType.Lightning, 10000, 10000), true,
                minion.MaxLife, 11, _ => 0, _ => 0, new Random(505)));
            double taken = Game.MonsterDotTaken(minion, DamageType.Chaos, 12);
            Assert.True(taken > 1);
            Game.TickEssenceAurasLocked(conn, 12);
            uint remaining = 9600u - (uint)Math.Floor(400 * taken);
            Assert.Equal(remaining, minion.Life);
            second.Dead = true;
            Game.TickEssenceAurasLocked(conn, 13);
            Assert.Equal(remaining, minion.Life);
            second.Dead = false;
            conn.Monsters.Remove(43);
            Assert.Equal(0, Step(12, 13)); // removed identities cannot keep dealing damage
            conn.Summons.Fighters.Clear();
            conn.Monsters.Remove(44);
            conn.Monsters[43] = second;
            Game.TickEssenceAurasLocked(conn, 14);
            Step(14, 15);
            using (Game.CleanupEssenceAuras()) { }
            Assert.Equal(0, Step(15, 16));
            Game.TickEssenceAurasLocked(conn, 20);
            var decay = Program.Items.Mods.ById("DecayOnHitEssenceMonster1")!;
            var decaySource = Source(310);
            decaySource.Mods = Game.MonsterModRolls.Value.Create([decay.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70);
            Game.ApplyMonsterDecayLocked(conn, 45, decaySource, false, 20, stats);
            Game.AdvancePlayerAilmentsLocked(conn, 20, 21, stats);
            Assert.Equal(9350u, conn.PlayerLife); // both degeneration owners reach Life in the same recovery step
            conn.PlayerLife = 1;
            Game.AdvancePlayerAilmentsLocked(conn, 21, 22, stats);
            Assert.Equal(0u, conn.PlayerLife);
            Assert.True(double.IsNegativeInfinity(Game.PlayerEssenceAuraStarted(conn)));
            Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(conn)));
        }
        finally { Game.ClearEssenceAuraExposure(conn); Game.ClearPlayerDecay(conn); conn.Closed = true; }
    }
}
