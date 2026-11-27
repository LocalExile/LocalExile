using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>The monster skill data from the client tables: monster-contacts v9 clips and named events, projectile
/// resolution, and object-types v14 StateMachine metadata with the server's S→C 0x2bc state update.</summary>
public sealed class MonsterSkillDataTests
{
    [Fact]
    public void Clip_events_projectiles_and_state_meta_load_from_the_client_tables()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var skills = Game.SkillsFor(player);
        MonsterCatalog.Variety Variety(string id) => Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(id));

        // 1. monster-contacts v9: .ao view_of with view_exclude_event (Sandspitter), item_animation OffHand using with
        //    several contacts and named events (Tora), effect_locked (Scarecrow) and a root @Alias (SpiderBossTalisman).
        var sandspitter = skills.MonsterContactPool("Metadata/Monsters/LeagueHarvest/Blue/HarvestSandspitterT1", "SpellProjectile");
        Assert.Equal(new[] { "attack1_01", "attack2_01" }, sandspitter!.Select(c => c.Animation));
        Assert.Equal(new int?[] { 359, 313 }, sandspitter!.Select(c => c.ContactMs));
        var tora = skills.MonsterContact("Metadata/Monsters/LeagueBetrayal/StandaloneTora", "SpellProjectile", 0)!;
        Assert.Equal(("dash_spray_bow_01", 8), (tora.Animation, tora.Contacts!.Count));
        Assert.NotNull(tora.FirstEvent("move"));
        var scarecrow = skills.MonsterContact("Metadata/Monsters/Skeletons/Scarecrow/ScarecrowPitchforkBoss", "SpellProjectile", 0)!;
        Assert.Equal(("attack_spear_01", (int?)600), (scarecrow.Animation, scarecrow.ContactMs));
        var spider = skills.MonsterContact("Metadata/Monsters/Spiders/SpiderBossTalisman", "SpellProjectile", 0)!;
        Assert.Equal(("attack2", (int?)499), (spider.Animation, spider.ContactMs));
        // Exact (name, ms, position) duplicates go; shots at one ms from distinct positions stay (Vox's shield throw 62 → 31).
        var voxThrow = skills.MonsterContactPool("Metadata/Monsters/LegionLeague/LegionTemplarGeneral", "Throw")![0].Events;
        Assert.Equal(62, voxThrow.Count(e => e.Name == "trigger_proj"));
        Assert.Equal(31, MonsterCombos.DedupeEvents(voxThrow).Count(e => e.Name == "trigger_proj"));

        // 2. Projectiles: a weapon attack's own monster_projectile_variation row (ScavengerThrow is variation 0); the
        //    shared-skill borrow main had stays the fallback; an art row missing its higher variant falls back to 0.
        Assert.Equal("Metadata/Projectiles/LegionMarakethRangedDash", skills.VariationProjectile(skills.EffectRow("LegionMarakethRangedDashProjectile"))?.Id);
        Assert.Equal("Metadata/Projectiles/Scavenger", skills.VariationProjectile(skills.EffectRow("ScavengerThrow"))?.Id);
        var fairgraves = skills.MonsterProjectile(skills.EffectRow("FairgravesThrownWeapon"));
        Assert.Equal(("Metadata/Projectiles/SpectralThrowFairgraves", "Melee"), (fairgraves?.Metadata, fairgraves?.AnimationKey));
        Assert.Equal("Metadata/Projectiles/SummonPhantasmFadingProjectile",
            skills.SharedProjectile(skills.EffectRow("SummonPhantasmFadingProjectile"))?.Id);
        Assert.NotNull(skills.SharedProjectile(skills.EffectRow("DeceleratingProjectile")));
        foreach (string id in new[] { "AncestralIkiahoArcticBreath", "FairgravesVaalThrownWeapon", "HandFingerVaalThrownWeaponBoss" })
            Assert.NotNull(skills.SkillArtProjectile(skills.EffectRow(id)));
        Assert.Equal("Metadata/Projectiles/LegionVaalSergeantSoulrend", skills.SkillArtProjectile(skills.EffectRow("LegionVaalSoulrend"))?.Id);

        // 3. object-types v14 state meta: client writers and listeners, the default-hidden flag with its enable value,
        //    and the Heist chain's ninth shared state (the client's comment-in-string quirk).
        var viper = Variety("Metadata/Monsters/AtlasExiles/BasiliskInfluenceMonsters/BasiliskFemaleViper");
        Assert.Equal("StateMachine", viper.ComponentsOrBase[viper.StateMachineComponentIndex]);
        Assert.Equal(-1, (viper with { Components = ["Positioned", "Life"] }).StateMachineComponentIndex);
        Assert.True(viper.StateMeta["arrow_visible"] is { HasClientListener: true, HasClientWriter: true });
        Assert.True(viper.StateMeta["visible"] is { DisablesRenderingAtDefault: true, EnableValue: 1 });
        var heist = Game.Monsters.All.First(v => v.StateMachine is not null
            && v.Chain.Any(c => c.Contains("HeistMonsterBase", StringComparison.OrdinalIgnoreCase)));
        Assert.True(heist.StateMachine!.SharedStates.Count >= 9);

        // 4. A server state change: no packet when nothing changes; otherwise 0x2bc with the override flag 0x02 on the
        //    state and never the client lock 0x01, and the late joiners' origin record follows.
        var templar = Variety("Metadata/Monsters/LegionLeague/LegionTemplarGeneral");
        Assert.True(templar.StateMeta["holy_blast"] is { HasClientListener: true, HasClientWriter: false });
        const uint monsterId = 0x7701;
        var states = templar.StateMachine!;
        var monster = new SpawnedMonster("Templar", new PlayerWalk(100, 100, 15, (_, _) => true), 60, 5, 1000) { Variety = templar, States = states };
        player.Instance.World.MonsterOrigins[monsterId] = new(0x12345678u,
            new MonsterObject(100, 100, 1000, templar.ComponentsOrBase, templar.AnimatedObject, MonsterRarity.Unique, templar.Name, states),
            (ushort)templar.Row, 80, MonsterRarity.Unique);
        Assert.False(MonsterStateMachine.TrySet(player, monsterId, monster, "holy_blast", 0, out _));
        Assert.False(MonsterStateMachine.TrySet(player, monsterId, monster, "nonexistent_state", 1, out _));
        Assert.True(MonsterStateMachine.TrySet(player, monsterId, monster, "holy_blast", 1, out var packet));
        Assert.Same(monster.States, player.Instance.World.MonsterOrigins[monsterId].Object.StateMachine);
        Assert.Equal((0x02, 0xbc, (byte)templar.StateMachineComponentIndex), (packet![0], packet[1], packet[14]));
        byte flags = packet[17 + 2 * states.IndexOf("holy_blast") + 1]; // one-byte value then flags per state from offset 17
        Assert.Equal(0x02, flags & 0x03);
    }
}
