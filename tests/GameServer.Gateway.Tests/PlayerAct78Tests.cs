using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class PlayerAct78Tests
{
    [Fact]
    public void Native_shots_receive_counts_and_art_without_changing_simulated_mortar_flights()
    {
        InGame(player =>
        {
            var catalog = Game.SkillsFor(player);
            foreach (string id in new[] { "Metadata/Monsters/Guardians/GuardianFire",
                "Metadata/Monsters/Skeletons/SkeletonCasterKnightFire",
                "Metadata/Monsters/incaminion/Fragment2", "Metadata/Monsters/Skeletons/SkeletonBow3" })
            {
                var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(id));
                int effect = Game.MavenNucleus.CombatEffect(catalog, variety);
                var attack = BossKits.AttackFor(player, variety, 58, effect, 0x4000) with { Ranged = true };
                var stats = Game.MonsterActionSkillStats(player, effect, 58, attack).ToDictionary(s => s.Item1, s => s.Item2);
                Assert.True(stats[Stat.VirtualTotalNumberOfProjectiles] >= 1);
                Assert.Equal(stats[Stat.VirtualTotalNumberOfProjectiles], stats[Stat.TotalNumberOfProjectilesToFire]);
                Assert.Equal(stats[Stat.VirtualTotalNumberOfProjectiles], stats[Stat.VirtualTotalNumberOfArrows]);
                var raw = catalog.EffectStats(effect, 58);
                int Raw(string name) => Program.Stats.Key(name) is uint key ? raw.FirstOrDefault(s => s.Item1 == key).Item2 : 0;
                int expected = Raw("number_of_projectiles_override") is > 0 and var n ? n
                    : Math.Max(1, Raw("base_number_of_projectiles")) + Raw("number_of_additional_projectiles");
                Assert.Equal(Math.Max(1, expected), stats[Stat.VirtualTotalNumberOfProjectiles]);
                // Native secondary projectile/skill settings must survive the final-count publication.
                foreach (var stat in raw.Where(s => s.Item1 != Stat.BaseSpellCastTimeMs
                    && s.Item1 != Stat.VirtualBaseSpellCastTimeMs && s.Item1 != Stat.HundredTimesCastsPerSecond))
                    if (Program.Stats.Name(stat.Item1) is not ("base_mana_cost" or "mana_cost")
                        && stat.Item1 != Stat.CastingSpell && !attack.Stats().Any(s => s.Item1 == stat.Item1))
                        Assert.True(stats.ContainsKey(stat.Item1));
            }
            var mortar = new MonsterAttack(0x4000, 1, 30, GameServer.Core.World.DamagePacket.Empty)
                { Ranged = true, Projectile = new("Metadata/Projectiles/SandspitterMortarProjectile", 100, false, 1)
                    { Count = 3, Variation = 183 } };
            var final = Game.MonsterActionSkillStats(player, 35, 58, mortar);
            uint artKey = Stat.SkillArtVariation;
            Assert.Equal(Game.MonsterProjectileSkillStats(player, 35, 58, mortar), final.Where(s => s.Item1 != artKey));
            Assert.Equal(21, Assert.Single(final, s => s.Item1 == artKey).Item2);
        });
    }

    static void InGame(Action<Player> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new Player(new(1, "native shots", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
