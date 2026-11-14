using GameServer.Modding;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A boss with no encounter controller of its own fights with the whole list of granted effects the tables give it
/// (BossKits), instead of walking up and swinging one default attack forever.
/// </summary>
public sealed class BossKitsTests
{
    [Theory]
    [InlineData("OriathanCitizenZombieMale1", 5196)]
    [InlineData("OriathanCitizenZombieMale4Boss", 5197)]
    public void The_living_Strand_zombies_cannot_select_their_death_explosion(string id, int explosion)
    {
        InGame(conn =>
        {
            var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/OriathanCitizen/" + id));
            var catalog = Game.SkillsFor(conn);
            Assert.Equal(1995, variety.AttackMs);
            Assert.Equal(406, Game.MavenNucleus.CombatEffect(catalog, variety));
            Assert.DoesNotContain(explosion, BossKits.Effects(catalog, variety));
            Assert.Equal(-1, Game.MavenNucleus.CombatEffect(catalog, variety with { GrantedEffects = [explosion] }));
        });
    }

    [Fact]
    public void A_campaign_boss_has_its_whole_kit_and_a_plain_monster_has_none()
    {
        InGame(conn =>
        {
            // Brutus, Lord Incarcerator (The Upper Prison): a bar of his own, six effects, three the client plays as a
            // hit of his own. The other three are shapes and triggers that answer to objects the server never creates,
            // and they are left out: a swing that hits for nothing only makes him stand still.
            var brutus = Game.Monsters.Find("Metadata/Monsters/Brute/BossBrute");
            Assert.NotNull(brutus);
            Assert.True(brutus.BossHealthBar);
            Assert.True(BossKits.IsCampaignBoss(brutus));
            int defaultEffect = brutus.GrantedEffects![0];
            var kit = BossKits.Kit(conn, brutus, 9, defaultEffect);
            Assert.Equal([2395, 4003, 491, 9282], kit.Select(k => k.Effect));
            // The effect his default attack already used stays first: a kit adds the rest of the list, it does not
            // replace the attack he had (Dominus' LightningSlam is live-verified).
            Assert.Equal(defaultEffect, kit[0].Effect);
            ushort[] ids = [.. kit.Select(k => k.Attack.SkillId)];
            ushort[] slots = [0x4000, 0x4001, 0x4002, 0x4003];
            Assert.Equal<ushort>(slots, ids);
            Assert.Equal(kit.Count, kit.Select(k => k.Effect).Distinct().Count());
            // Each of them reaches the actor under the id the order will name: what 0x1ab can point at.
            ushort[] granted = [.. BossKits.SkillsOf(conn, brutus, 9, kit).Select(s => s.Id)];
            Assert.Equal<ushort>(ids, granted);
            // The specials the tables give a cooldown keep it: the hook and the ground slam before the swings.
            Assert.Equal([6d, 6d], kit.Take(2).Select(k => k.CooldownSeconds));
            Assert.All(kit.Skip(2), k => Assert.Equal(0, k.CooldownSeconds));

            // Fire Fury (The Coast) has no life bar and is still one of the campaign's bosses: the small ones are the
            // ones that walked up and swung one attack for the whole game.
            var fury = Game.Monsters.Find("Metadata/Monsters/Cannibal/CannibalFemaleBossWitch");
            Assert.NotNull(fury);
            Assert.False(fury.BossHealthBar);
            Assert.True(BossKits.IsCampaignBoss(fury));
            Assert.True(BossKits.Kit(conn, fury, 2).Count > 1);

            // A plain monster: no list of its own, and not a boss of any area.
            var corpse = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite");
            Assert.NotNull(corpse);
            Assert.False(BossKits.IsCampaignBoss(corpse));
            Assert.Empty(BossKits.Kit(conn, corpse, 1, 406));

            // Arakaali's arena-laser activators need the encounter's turret objects. Generic AI has none; calling
            // the activator with an empty turret list crashes the native client, even while the boss is at full life.
            var arakaali = Assert.IsType<MonsterCatalog.Variety>(
                Game.Monsters.Find("Metadata/Monsters/VaalSpiderGod/Arakaali"));
            var catalog = Game.SkillsFor(conn);
            int[] laserDrivers = [6879, 7802, 7805];
            Assert.All(laserDrivers, effect => Assert.Contains(effect, arakaali.GrantedEffects!));
            var arakaaliKit = BossKits.Kit(conn, arakaali, 55, Game.MavenNucleus.CombatEffect(catalog, arakaali));
            Assert.DoesNotContain(arakaaliKit, entry => laserDrivers.Contains(entry.Effect));
            // Standalone mortar and melee attacks remain playable; this does not disable the encounter or its actor.
            Assert.Contains(arakaaliKit, entry => entry.Effect == 4438);
            Assert.Contains(arakaaliKit, entry => entry.Effect == 208);
        });
    }

    [Fact]
    public void A_kit_plays_a_ready_special_first_and_rotates_the_rest()
    {
        // A swing with no cooldown, a second one without a cooldown, and a special that reaches further and is ready
        // again 30 seconds after each use.
        MonsterKitSkill[] kit =
        [
            new(100, new(0x4000, 1.5, 8, GameServer.Core.World.DamagePacket.Empty), 0),
            new(200, new(0x4001, 1.5, 8, GameServer.Core.World.DamagePacket.Empty), 0),
            new(300, new(0x4002, 2, 60, GameServer.Core.World.DamagePacket.Empty), 30),
        ];
        var boss = new SpawnedMonster("boss", new(0, 0, 1), 60, 2, maxLife: 100) { Kit = kit, Attack = kit[0].Attack };
        // Out of the swings' reach but inside the special's: the special is the only one that can act, so it does.
        Assert.Equal(0x4002, BossKits.Choose(boss, 0, 40)!.SkillId);
        // Ready, it also takes the turn in melee reach, ahead of the two swings.
        Assert.Equal(0x4002, BossKits.Choose(boss, 0, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[2].Attack, 0);
        // Its cooldown is not out yet: with nothing else to prove, the default swing is the tie-break.
        Assert.Equal(0x4000, BossKits.Choose(boss, 1, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[0].Attack, 1);
        // Used at 1, the default swing steps aside for the second one, which has not been used yet.
        Assert.Equal(0x4001, BossKits.Choose(boss, 2, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[1].Attack, 2);
        // Both used, neither special ready: the swing used longest ago takes it again.
        Assert.Equal(0x4000, BossKits.Choose(boss, 2, 2)!.SkillId);
        // The special is spent: out of the swings' reach nothing can act, and the boss keeps the attack it had (the
        // walk-up brings it within reach).
        Assert.Equal(0x4000, BossKits.Choose(boss, 3, 40)!.SkillId);
        // Ready again at 32, it takes the turn ahead of the two without a cooldown, even in melee reach.
        Assert.Equal(0x4002, BossKits.Choose(boss, 32, 2)!.SkillId);
        // Nothing reaches a player that far away: it keeps the attack it had and walks up.
        Assert.Equal(0x4000, BossKits.Choose(boss, 40, 500)!.SkillId);
    }

    /// <summary>Runs the body with the game module mounted on a connection of its own, as BossStageTests does.</summary>
    static void InGame(Action<ClientState> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new ClientState(new(1, "kit", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
