using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class PlayerAct78Tests
{
    [Fact]
    public void Native_shots_receive_final_counts_without_changing_simulated_mortars()
    {
        InGame(conn =>
        {
            var catalog = Game.SkillsFor(conn);
            foreach (string id in new[] { "Metadata/Monsters/Guardians/GuardianFire",
                "Metadata/Monsters/Skeletons/SkeletonCasterKnightFire",
                "Metadata/Monsters/incaminion/Fragment2", "Metadata/Monsters/Skeletons/SkeletonBow3" })
            {
                var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(id));
                int effect = Game.MavenNucleus.CombatEffect(catalog, variety);
                var attack = BossKits.AttackFor(conn, variety, 58, effect, 0x4000) with { Ranged = true };
                var stats = Game.MonsterActionSkillStats(conn, effect, 58, attack).ToDictionary(s => s.Item1, s => s.Item2);
                Assert.True(stats[StatKeys.VirtualTotalProjectiles] >= 1);
                Assert.Equal(stats[StatKeys.VirtualTotalProjectiles], stats[StatKeys.TotalProjectilesToFire]);
                Assert.Equal(stats[StatKeys.VirtualTotalProjectiles], stats[StatKeys.VirtualTotalArrows]);
                var raw = catalog.EffectStats(effect, 58);
                int Raw(string name) => Program.Stats.Key(name) is uint key ? raw.FirstOrDefault(s => s.Item1 == key).Item2 : 0;
                int expected = Raw("number_of_projectiles_override") is > 0 and var n ? n
                    : Math.Max(1, Raw("base_number_of_projectiles")) + Raw("number_of_additional_projectiles");
                Assert.Equal(Math.Max(1, expected), stats[StatKeys.VirtualTotalProjectiles]);
                // Native secondary projectile/skill settings must survive the final-count publication.
                foreach (var stat in raw.Where(s => s.Item1 != StatKeys.BaseSpellCastTimeMs
                    && s.Item1 != StatKeys.VirtualBaseSpellCastTimeMs && s.Item1 != StatKeys.HundredTimesCastsPerSecond))
                    if (Program.Stats.Name(stat.Item1) is not ("base_mana_cost" or "mana_cost")
                        && stat.Item1 != StatKeys.CastingSpell && !attack.Stats().Any(s => s.Item1 == stat.Item1))
                        Assert.True(stats.ContainsKey(stat.Item1));
            }
            var mortar = new MonsterAttack(0x4000, 1, 30, GameServer.Core.World.DamagePacket.Empty)
                { Ranged = true, Projectile = new("Metadata/Projectiles/SandspitterMortarProjectile", 100, false, 1)
                    { Count = 3, Variation = 183 } };
            Assert.Equal(Game.MonsterProjectileSkillStats(conn, 35, 58, mortar),
                Game.MonsterActionSkillStats(conn, 35, 58, mortar));
        });
    }

    [Fact]
    public void A_new_soul_absorber_plays_its_entrance_but_a_later_visit_does_not()
    {
        var npcs = NpcCatalog.Load();
        foreach (string id in new[] { "Metadata/NPC/Act6/SinTukohama", "Metadata/NPC/Act7/SinArakaali",
            "Metadata/NPC/Act8/SinYugul", "Metadata/NPC/Act8/SinSolarisLunaris" })
        {
            var npc = Assert.IsType<NpcCatalog.Npc>(npcs.Find(id));
            var rule = new Quests.AppearsRule("area", "boss", id, null);
            Assert.True(Game.AppearingNpc("area", rule, npc, 10, 10, arriving: true).Npc.TransitionState == 0, id);
            Assert.Equal(npc.TransitionState, Game.AppearingNpc("area", rule, npc, 10, 10).Npc.TransitionState);
        }
        var town = Assert.IsType<NpcCatalog.Npc>(npcs.Find("Metadata/NPC/Act8/SinTown"));
        Assert.Equal(town.TransitionState, Game.AppearingNpc("area",
            new("area", "boss", town.Id, null), town, 10, 10, arriving: true).Npc.TransitionState);
    }

    static void InGame(Action<ClientState> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new ClientState(new(1, "native shots", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
